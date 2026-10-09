using System.Windows;

namespace SampleAddin
{
    public partial class HelloWindow : Window
    {
        public HelloWindow(string documentTitle)
        {
            InitializeComponent();
            DocumentText.Text = "Document: " + documentTitle;
            AssemblyText.Text = "Loaded as " + GetType().Assembly.GetName().Name;
        }
    }
}
