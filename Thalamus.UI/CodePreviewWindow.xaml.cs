using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Thalamus.UI;

public partial class CodePreviewWindow : Window
{
    private readonly string _code;

    public CodePreviewWindow(string code)
    {
        InitializeComponent();
        _code = code;
        CodeTextBox.Text = code;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(CodeTextBox.Text);
        MessageBox.Show("Code copied to clipboard.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save C# Code",
            Filter = "C# Files (*.cs)|*.cs|All Files (*.*)|*.*",
            DefaultExt = ".cs",
            FileName = "ThalamusLogic.cs"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllText(dialog.FileName, CodeTextBox.Text);
                MessageBox.Show($"Code saved to:\n{dialog.FileName}", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
