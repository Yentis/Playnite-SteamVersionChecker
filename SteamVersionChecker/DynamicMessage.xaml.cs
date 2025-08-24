#nullable enable

using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SteamVersionChecker
{
    /// <summary>
    /// Interaction logic for DynamicMessage.xaml
    /// </summary>
    public partial class DynamicMessage : UserControl
    {
        public DynamicMessage()
        {
            InitializeComponent();
        }

        public void SetMessage(string message, int closeAfter = 0)
        {
            txtDynamicMessage.Text = message;

            if (closeAfter > 0)
            {
                _ = CloseWithDelay(closeAfter);
            }
        }

        private async Task CloseWithDelay(int delay)
        {
            await Task.Delay(delay);
            Window.GetWindow(this).Close();
        }
    }
}
