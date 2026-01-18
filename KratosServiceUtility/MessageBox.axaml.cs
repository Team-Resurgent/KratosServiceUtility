using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace KratosServiceUtility
{
    public enum MessageBoxButtons
    {
        Ok,
        YesNo
    }

    public enum MessageBoxIcon
    {
        None,
        Information,
        Warning,
        Error
    }

    public enum MessageBoxResult
    {
        None,
        Ok,
        Yes,
        No
    }

    public partial class MessageBox : Window
    {
        public static readonly StyledProperty<string> MessageProperty =
            AvaloniaProperty.Register<MessageBox, string>(nameof(Message), "");

        public static readonly StyledProperty<MessageBoxButtons> ButtonsProperty =
            AvaloniaProperty.Register<MessageBox, MessageBoxButtons>(nameof(Buttons), MessageBoxButtons.Ok);

        public static readonly StyledProperty<MessageBoxIcon> IconProperty =
            AvaloniaProperty.Register<MessageBox, MessageBoxIcon>(nameof(Icon), MessageBoxIcon.None);

        public new string Title
        {
            get => base.Title ?? "";
            set => base.Title = value;
        }

        public string Message
        {
            get => GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public MessageBoxButtons Buttons
        {
            get => GetValue(ButtonsProperty);
            set => SetValue(ButtonsProperty, value);
        }

        public MessageBoxIcon Icon
        {
            get => GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public bool ShowOk => Buttons == MessageBoxButtons.Ok;
        public bool ShowYes => Buttons == MessageBoxButtons.YesNo;
        public bool ShowNo => Buttons == MessageBoxButtons.YesNo;

        public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

        public MessageBox()
        {
            InitializeComponent();
            DataContext = this;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            // Update computed properties when Buttons changes
            if (change.Property == ButtonsProperty)
            {
                RaisePropertyChanged(ShowOkProperty, !ShowOk, ShowOk);
                RaisePropertyChanged(ShowYesProperty, !ShowYes, ShowYes);
                RaisePropertyChanged(ShowNoProperty, !ShowNo, ShowNo);
            }
        }

        // Create observable properties for the computed values
        public static readonly DirectProperty<MessageBox, bool> ShowOkProperty =
            AvaloniaProperty.RegisterDirect<MessageBox, bool>(
                nameof(ShowOk),
                o => o.ShowOk);

        public static readonly DirectProperty<MessageBox, bool> ShowYesProperty =
            AvaloniaProperty.RegisterDirect<MessageBox, bool>(
                nameof(ShowYes),
                o => o.ShowYes);

        public static readonly DirectProperty<MessageBox, bool> ShowNoProperty =
            AvaloniaProperty.RegisterDirect<MessageBox, bool>(
                nameof(ShowNo),
                o => o.ShowNo);

        private void OkButton_Click(object? sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Ok;
            Close();
        }

        private void YesButton_Click(object? sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Yes;
            Close();
        }

        private void NoButton_Click(object? sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.No;
            Close();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            string iconText = "";
            string iconColor = "#e5e7eb";

            switch (Icon)
            {
                case MessageBoxIcon.Information:
                    iconText = "ℹ";
                    iconColor = "#0ea5e9";
                    break;
                case MessageBoxIcon.Warning:
                    iconText = "⚠";
                    iconColor = "#f59e0b";
                    break;
                case MessageBoxIcon.Error:
                    iconText = "✕";
                    iconColor = "#ef4444";
                    break;
            }

            IconTextBlock.Text = iconText;
            IconTextBlock.Foreground = new SolidColorBrush(Color.Parse(iconColor));
            IconTextBlock.IsVisible = !string.IsNullOrEmpty(iconText);
        }

        public static async Task<MessageBoxResult> Show(Window? parent, string message, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            var msgBox = new MessageBox
            {
                Title = title,
                Message = message,
                Buttons = buttons,
                Icon = icon
            };

            if (parent != null)
            {
                await msgBox.ShowDialog(parent);
            }
            else
            {
                msgBox.Show();
                // Wait for the window to close
                var tcs = new TaskCompletionSource<MessageBoxResult>();
                msgBox.Closed += (s, e) => tcs.SetResult(msgBox.Result);
                return await tcs.Task;
            }

            return msgBox.Result;
        }
    }
}

