using System.Diagnostics;
using System.Windows;

namespace Thalamus.UI;

public partial class TrialPopupWindow : Window
{
    public TrialPopupWindow()
    {
        InitializeComponent();
    }

    private void ProButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://dreamcraftstudio.org")
            { UseShellExecute = true });
        Close();
    }

    private void TinkerButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
