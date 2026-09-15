using System.Windows.Controls;

namespace YumeShelf.Presentation;
public partial class AiAssistantPage : System.Windows.Controls.UserControl
{
    public AiAssistantPage()
    {
        InitializeComponent();
    }

    private void ConversationScrolled(object sender, ScrollChangedEventArgs e)
    {
        // Keep the reader's position unless they were already at the bottom.
        if (e.ExtentHeightChange > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - e.ExtentHeightChange - 24)
            ConversationScroll.ScrollToEnd();
    }
}
