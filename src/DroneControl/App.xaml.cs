using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DroneControl;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        window.Show();
        if (e.Args.Length == 2 && e.Args[0] == "--render-check")
        {
            window.UpdateLayout();
            var image = new RenderTargetBitmap(
                (int)window.ActualWidth,
                (int)window.ActualHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var outputFile = File.Create(e.Args[1]))
            {
                encoder.Save(outputFile);
            }

            Shutdown();
        }
    }
}
