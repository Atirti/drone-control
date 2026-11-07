using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DroneControl;

/// <summary>Связывает WPF-контролы, аналоговый ввод и UDP-клиент.</summary>
public partial class MainWindow : Window
{
    private readonly DispatcherTimer controlTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50)
    };
    private readonly FlightInput flightInput = new();
    private DroneLink? droneLink;
    private FlightFlags activeCommand;
    private DateTime commandExpiresAt;
    private DateTime nextHeartbeatAt;
    private bool isTickRunning;
    private bool isDisconnecting;
    private bool isCloseComplete;
    private bool shouldSendNeutral;
    private int isFrameQueued;

    public MainWindow()
    {
        InitializeComponent();
        LeftStick.ValueChanged += position => UpdateScreenStick(position, isLeft: true);
        RightStick.ValueChanged += position => UpdateScreenStick(position, isLeft: false);
        controlTimer.Tick += Tick;
        controlTimer.Start();
    }

    private void Notice(string message) => NoticeText.Text = message;

    private async void ConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (droneLink != null)
        {
            await Disconnect();
            return;
        }

        try
        {
            var assembler = new BlFrames();
            var connection = new DroneLink(DroneProfile.Bl, "192.168.169.1", 8800, false);
            droneLink = connection;
            connection.Reply += packet =>
            {
                var jpeg = assembler.Push(packet);
                if (jpeg != null)
                {
                    ShowFrame(connection, jpeg);
                }

                return assembler.Acknowledgement;
            };
            connection.Error += message => Dispatcher.BeginInvoke(() =>
            {
                if (droneLink == connection)
                {
                    Armed.IsChecked = false;
                    Notice("Ошибка связи: " + message);
                }
            });
            nextHeartbeatAt = DateTime.MinValue;
            ConnectButton.Content = "Отключить";
            StopButton.IsEnabled = true;
            StatusText.Text = "Подключение…";
        }
        catch (Exception ex)
        {
            Notice("Не удалось подключиться: " + ex.Message);
        }
    }

    // Защита от повторного входа: async-обработчик таймера может не закончиться за 50 мс.
    private async void Tick(object? sender, EventArgs e)
    {
        if (isTickRunning || isDisconnecting)
        {
            return;
        }

        isTickRunning = true;
        try
        {
            Armed.IsEnabled = droneLink?.Responding == true;
            if (droneLink == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now >= nextHeartbeatAt)
            {
                await droneLink.Send(Protocol.Heartbeat(DroneProfile.Bl));
                nextHeartbeatAt = now.AddMilliseconds(110);
            }

            if (!droneLink.Responding && Armed.IsChecked == true)
            {
                Armed.IsChecked = false;
                Notice("Связь потеряна. Управление выключено.");
            }

            bool emergency = activeCommand == FlightFlags.Stop && now < commandExpiresAt;
            if (Armed.IsChecked == true || emergency || shouldSendNeutral)
            {
                if (!IsActive || Armed.IsChecked != true)
                {
                    ResetInput();
                }

                FlightFlags command = Armed.IsChecked == true && now < commandExpiresAt
                    ? activeCommand
                    : FlightFlags.None;
                var packet = emergency
                    ? Protocol.Flight(throttle: 0, flags: FlightFlags.Stop)
                    : flightInput.Packet(Power.Value, command);
                await droneLink.Control(packet);
                shouldSendNeutral = false;
            }

            StatusText.Text = droneLink.Responding ? "Подключён" : "Ожидание ответа дрона…";
        }
        catch (Exception ex)
        {
            Armed.IsChecked = false;
            activeCommand = 0;
            Notice("Ошибка связи: " + ex.Message);
        }
        finally
        {
            isTickRunning = false;
        }
    }

    private void UpdateScreenStick(StickPosition position, bool isLeft)
    {
        if (Armed.IsChecked != true || !IsActive || isDisconnecting)
        {
            return;
        }

        if (isLeft)
        {
            flightInput.SetLeft(position);
        }
        else
        {
            flightInput.SetRight(position);
        }
    }

    private void RefreshInputAvailability()
    {
        bool enabled = Armed.IsChecked == true;
        LeftStick.IsEnabled = enabled;
        RightStick.IsEnabled = enabled;
        FlightControls.IsEnabled = enabled;
    }

    private void ResetInput()
    {
        LeftStick?.ResetInput();
        RightStick?.ResetInput();
        flightInput.Release();
    }

    private void ArmChanged(object sender, RoutedEventArgs e)
    {
        ResetInput();
        activeCommand = 0;
        if (FlightControls != null)
        {
            RefreshInputAvailability();
        }

        if (Armed.IsChecked != true)
        {
            shouldSendNeutral = droneLink?.ControlsSent > 0;
        }
    }

    private void FocusLost(object? sender, EventArgs e)
    {
        // ArmChanged очищает команду; остановку сохраняем даже при смене фокуса.
        bool stopping = activeCommand == FlightFlags.Stop;
        Armed.IsChecked = false;
        ResetInput();
        if (stopping)
        {
            activeCommand = FlightFlags.Stop;
        }
        if (activeCommand != FlightFlags.Stop)
        {
            activeCommand = 0;
        }

        shouldSendNeutral = droneLink?.ControlsSent > 0;
    }

    private void ActionClicked(object sender, RoutedEventArgs e)
    {
        if (droneLink?.Responding != true || Armed.IsChecked != true)
        {
            return;
        }

        ResetInput();
        RefreshInputAvailability();
        activeCommand = (FlightFlags)byte.Parse((string)((Button)sender).Tag);
        commandExpiresAt = DateTime.UtcNow.AddSeconds(1);
        Notice(activeCommand == FlightFlags.Takeoff ? "Отправляется команда взлёта." : "Отправляется команда посадки.");
    }

    private void EmergencyClicked(object sender, RoutedEventArgs e)
    {
        ResetInput();
        Armed.IsChecked = false;
        activeCommand = FlightFlags.Stop;
        commandExpiresAt = DateTime.UtcNow.AddSeconds(1);
        Notice("Отправляется остановка моторов. При потере связи доставка не гарантируется.");
    }

    private async Task Disconnect()
    {
        if (isDisconnecting)
        {
            return;
        }

        isDisconnecting = true;
        ConnectButton.IsEnabled = false;
        try
        {
            ResetInput();
            Armed.IsChecked = false;
            activeCommand = 0;
            while (isTickRunning)
            {
                await Task.Delay(10);
            }

            if (droneLink != null)
            {
                var connectionToClose = droneLink;
                droneLink = null;
                try
                {
                    if (connectionToClose.ControlsSent > 0)
                    {
                        await connectionToClose.Control(Protocol.Flight());
                        await connectionToClose.Control([101]);
                    }
                }
                catch (Exception ex)
                {
                    Notice(ex.Message);
                }

                await connectionToClose.DisposeAsync();
            }

            shouldSendNeutral = false;
            VideoImage.Source = null;
            VideoHint.Visibility = Visibility.Visible;
            StatusText.Text = "Не подключён";
            Armed.IsEnabled = false;
            StopButton.IsEnabled = false;
            ConnectButton.Content = "Подключить";
        }
        finally
        {
            isDisconnecting = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (isCloseComplete)
        {
            return;
        }

        e.Cancel = true;
        if (isDisconnecting)
        {
            return;
        }

        await Disconnect();
        controlTimer.Stop();
        isCloseComplete = true;
        Close();
    }

    // Не накапливаем очередь кадров, если UI рисует медленнее камеры.
    private void ShowFrame(DroneLink source, byte[] jpeg)
    {
        if (Interlocked.Exchange(ref isFrameQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (droneLink != source || isDisconnecting)
                {
                    return;
                }

                using var stream = new MemoryStream(jpeg);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                VideoImage.Source = image;
                VideoHint.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
            {
                Notice("Не удалось прочитать видеокадр.");
            }
            finally
            {
                Interlocked.Exchange(ref isFrameQueued, 0);
            }
        });
    }

    private void SaveSnapshot(object sender, RoutedEventArgs e)
    {
        if (VideoImage.Source is not BitmapSource image)
        {
            Notice("Для снимка дождитесь видео.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG|*.png",
            FileName = $"drone-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(dialog.FileName);
            encoder.Save(file);
            Notice("Снимок сохранён.");
        }
        catch (Exception ex)
        {
            Notice(ex.Message);
        }
    }
}
