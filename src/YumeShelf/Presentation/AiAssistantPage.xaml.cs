using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace YumeShelf.Presentation;
public partial class AiAssistantPage : System.Windows.Controls.UserControl
{
    private bool _composing;
    public AiAssistantPage()
    {
        InitializeComponent();
        TextCompositionManager.AddPreviewTextInputStartHandler(QueryEditor, (_, _) => _composing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(QueryEditor, (_, _) => _composing = true);
        QueryEditor.PreviewTextInput += (_, _) => Dispatcher.BeginInvoke(() => _composing = false, DispatcherPriority.Background);
        QueryEditor.LostKeyboardFocus += (_, _) => _composing = false;
        QueryEditor.PreviewKeyDown += (_, args) =>
        {
            if (!ShouldSend(args.Key, Keyboard.Modifiers, _composing)) return;
            if (DataContext is AiAssistantViewModel vm && vm.SendCommand.CanExecute(null))
            { args.Handled = true; vm.SendCommand.Execute(null); }
        };
    }

    public static bool ShouldSend(Key key, ModifierKeys modifiers, bool composing)
        => key == Key.Enter && modifiers == ModifierKeys.None && !composing;

    private void ConversationScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Keep the reader's position unless they were already at the bottom.
        if (e.ExtentHeightChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 24)
            ConversationScroll.ScrollToEnd();
    }
}
