using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WwanMessenger.App.ViewModels;

namespace WwanMessenger.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;
    private ConversationViewModel? _observedConversation;
    private ScrollViewer? _transcriptScroller;
    private ScrollViewer? _consoleScroller;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // buttery wheel: intercept the notch jump before the inner ScrollViewer
        // can scroll by "lines" (tunneling reaches the ListBox first).
        Transcript.PreviewMouseWheel += OnTranscriptPreviewWheel;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) return;

        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.PropertyChanged += OnVmPropertyChanged;
        // Defer the scroll: scrolling synchronously inside CollectionChanged desyncs
        // the virtualizing ItemContainerGenerator ("ItemsControl is inconsistent").
        ((INotifyCollectionChanged)_vm.ConsoleLines).CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(ScrollConsoleToEnd, DispatcherPriority.Background);

        if (_observedConversation is not null)
            ObserveConversation(_observedConversation);

        // Auto-bring-up the channel so the console is alive (simulator or live modem).
        if (!_vm.Connected)
            await _vm.ConnectAsync();

        // After first layout the inner scroll viewers exist — cache them, then
        // land the transcript on the newest message.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _transcriptScroller = FindScrollViewer(Transcript);
            _consoleScroller = FindScrollViewer(ConsoleView);
            ScrollTranscriptToEnd();
        }), DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer sv) return sv;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedConversation))
            ObserveConversation(_vm?.SelectedConversation);
    }

    private void ObserveConversation(ConversationViewModel? conversation)
    {
        if (_observedConversation is not null)
            ((INotifyCollectionChanged)_observedConversation.Messages).CollectionChanged -= OnMessagesChanged;
        _observedConversation = conversation;
        if (conversation is not null)
        {
            ((INotifyCollectionChanged)conversation.Messages).CollectionChanged += OnMessagesChanged;
            Dispatcher.BeginInvoke(ScrollTranscriptToEnd, DispatcherPriority.Background);
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.BeginInvoke(ScrollTranscriptToEnd, DispatcherPriority.Background);
    }

    /// <summary>Follow the newest message. Uses the inner ScrollViewer rather
    /// than ScrollIntoView: on virtualized lists ScrollIntoView merely brings
    /// the item into view — often pinned to the TOP of the viewport — which is
    /// the classic "chat doesn't scroll to the bottom" bug. Now pixel-animated:
    /// a short glide when the reader is scrolled up, an instant snap when
    /// already parked at the bottom (a new message lands with zero motion).</summary>
    private void ScrollTranscriptToEnd()
    {
        _transcriptScroller ??= FindScrollViewer(Transcript);
        if (_transcriptScroller is not null)
        {
            SmoothScroller.ScrollToEndSmooth(_transcriptScroller);
            return;
        }
        if (Transcript.Items.Count > 0)
            Transcript.ScrollIntoView(Transcript.Items[^1]); // pre-layout fallback
    }

    /// <summary>Turns a raw wheel notch into an eased pixel glide instead of
    /// the default line-count jump-cut (the ListBox scrolls in pixels via
    /// ScrollViewer.CanContentScroll="False"). Marked handled so the inner
    /// ScrollViewer does not also apply its default notch scroll.</summary>
    private void OnTranscriptPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        _transcriptScroller ??= FindScrollViewer(Transcript);
        if (_transcriptScroller is not { } sv) return; // not laid out yet — default behavior
        e.Handled = true;
        SmoothScroller.Wheel(sv, Math.Clamp(e.Delta / 120.0, -6, 6));
    }

    private void ScrollConsoleToEnd()
    {
        _consoleScroller ??= FindScrollViewer(ConsoleView);
        if (_consoleScroller is not null)
        {
            _consoleScroller.ScrollToEnd();
            return;
        }
        if (ConsoleView.Items.Count > 0)
            ConsoleView.ScrollIntoView(ConsoleView.Items[^1]);
    }

    // PreviewKeyDown, not KeyDown: in an AcceptsReturn TextBox the framework's
    // text editor consumes Enter before KeyDown fires - tunneling gets us first.
    private void OnComposeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers == ModifierKeys.Shift) return;
        if (_vm is null) return;

        if (_vm.SendCommand.CanExecute(null))
        {
            _vm.SendCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // nothing to send - swallow blank-Enter so it can't produce empty lines
        if (string.IsNullOrWhiteSpace(_vm.ComposeText))
            e.Handled = true;
    }

    private void OnNewChannel(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dialog = new NewChannelDialog { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.AddChannel(dialog.NumberInput.Text, dialog.NameInput.Text);
    }

    /// <summary>Pencil in the transcript header: rename the selected contact
    /// via a small modal (Enter=OK, Esc=cancel). An emptied name falls back to
    /// the raw number; the rail Title/Initials update live via the VM.</summary>
    private void OnRenameContact(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedConversation is not { } conv) return;
        var dialog = new RenameContactDialog(conv.Model.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true)
            _vm.RenameContact(conv, dialog.NameInput.Text);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _vm?.Dispose();
    }

    // ---- custom window chrome ----------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            e.ButtonState != MouseButtonState.Pressed) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // un-maximize under the cursor like production apps do
            WindowState = WindowState.Normal;
            // re-anchor: keep the cursor on the title bar after the restore
            Top = e.GetPosition(this).Y + Top - ActualHeight / 2;
            Left = e.GetPosition(this).X + Left - ActualWidth / 2;
        }
        DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // when maximized, pull content inside the (hidden) resize frame
        Padding = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConversationViewModel cvm } && _vm is not null)
            _vm.TogglePin(cvm);
    }
}

/// <summary>
/// Dependency-free smooth scrolling for a pixel-mode ScrollViewer
/// (CanContentScroll="False"): animates a private attached "Offset" property
/// with a WPF DoubleAnimation and applies every animated tick through
/// ScrollToVerticalOffset (the viewer's own VerticalOffset is not directly
/// animatable). Exactly ONE animation per viewer is ever alive — starting a
/// new one cancels the previous (pileup guard) and retargets from the CURRENT
/// live offset, so rapid wheel ticks, end-glide arrivals and direction
/// reversals all blend into a single continuous motion.
/// </summary>
internal static class SmoothScroller
{
    private const double WheelStepPx = 90;   // travel per 120-delta wheel notch
    private static readonly TimeSpan WheelDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan EndDuration = TimeSpan.FromMilliseconds(200);
    private const double NearBottomPx = 80;  // "parked at the bottom" snap band
    private const double Epsilon = 0.5;      // don't animate sub-pixel moves

    private static readonly HashSet<ScrollViewer> Animating = new();
    private static readonly HashSet<ScrollViewer> SuppressSnapBack = new();
    private static readonly Dictionary<ScrollViewer, double> PendingTarget = new();

    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroller),
        new PropertyMetadata(0.0, OnOffsetChanged));

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if (SuppressSnapBack.Remove(sv)) return; // cancel frame snapping back to base 0 — swallow it
        sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    /// <summary>Wheel glide. Each notch EXTENDS the in-flight target instead
    /// of restarting from a stale offset, so fast spinning accelerates the
    /// glide instead of stuttering through restarts.</summary>
    public static void Wheel(ScrollViewer sv, double notches)
    {
        double from = PendingTarget.TryGetValue(sv, out var pending) ? pending : sv.VerticalOffset;
        Animate(sv, Math.Clamp(from + notches * WheelStepPx, 0, sv.ScrollableHeight), WheelDuration);
    }

    /// <summary>New-message arrival: glide down to the newest message over
    /// ~200ms — unless the reader is already within ~80px of the bottom, in
    /// which case snap instantly (no motion under a parked reader).</summary>
    public static void ScrollToEndSmooth(ScrollViewer sv)
    {
        if (sv.ScrollableHeight - sv.VerticalOffset <= NearBottomPx)
        {
            Stop(sv);
            sv.ScrollToEnd();
            return;
        }
        Animate(sv, sv.ScrollableHeight, EndDuration);
    }

    private static void Animate(ScrollViewer sv, double target, TimeSpan duration)
    {
        if (Math.Abs(sv.VerticalOffset - target) <= Epsilon)
        {
            Stop(sv);
            sv.ScrollToVerticalOffset(target);
            return;
        }

        Stop(sv); // pileup guard: at most one live animation per viewer

        var animation = new DoubleAnimation(sv.VerticalOffset, target, duration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            Animating.Remove(sv);
            PendingTarget.Remove(sv);
        };
        Animating.Add(sv);
        PendingTarget[sv] = target;
        sv.BeginAnimation(OffsetProperty, animation);
    }

    /// <summary>Cancels any in-flight glide. BeginAnimation(null) reasserts
    /// the base value (0), which would teleport the callback to the very top;
    /// that one synthetic change notification is suppressed instead.</summary>
    public static void Stop(ScrollViewer sv)
    {
        PendingTarget.Remove(sv);
        if (!Animating.Remove(sv)) return;
        if (Math.Abs((double)sv.GetValue(OffsetProperty)) > 0.01)
            SuppressSnapBack.Add(sv);
        sv.BeginAnimation(OffsetProperty, null);
    }
}
