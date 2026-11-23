using System.Windows;

namespace Rtf;

public partial class StartupErrorWindow : Window
{
    public StartupErrorWindow(string details)
    {
        InitializeComponent();
        Details.Text = details;
    }
}
