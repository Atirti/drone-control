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
    private readonly SdlGamepad gamepad = new();
    private readonly GamepadInput gamepadInput = new();
    private readonly GamepadButtonInput gamepadButtons = new();
    private DroneLink? droneLink;
    private FlightFlags activeCommand;
    private long commandExpiresAt;
    private long nextHeartbeatAt;
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
            var connection = new DroneLink("192.168.169.1", 8800);
            droneLink = connection;
            connection.Reply = packet =>
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
            nextHeartbeatAt = 0;
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
            try
            {
                UpdateGamepadInput();
            }
            catch (Exception ex)
            {
                Armed.IsChecked = false;
                ResetInput();
                gamepadButtons.Reset();
                Notice("Ошибка ввода: " + ex.Message);
                // Ошибка геймпада не должна блокировать heartbeat и аварийную остановку.
            }
            if (droneLink == null)
            {
                return;
            }

            long now = Environment.TickCount64;
            if (now >= nextHeartbeatAt)
            {
                await droneLink.Send(Protocol.Heartbeat());
                nextHeartbeatAt = now + 110;
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
                // Не затираем новый запрос нейтрали, пришедший во время await.
                shouldSendNeutral = false;
                await droneLink.Control(packet);
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
        if (IsGamepadMode || Armed.IsChecked != true || !IsActive || isDisconnecting)
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
        // В режиме геймпада стики служат индикаторами даже без подключения к дрону.
        LeftStick.IsEnabled = enabled || IsGamepadMode;
        RightStick.IsEnabled = enabled || IsGamepadMode;
        LeftStick.IsHitTestVisible = enabled && !IsGamepadMode;
        RightStick.IsHitTestVisible = enabled && !IsGamepadMode;
        FlightControls.IsEnabled = enabled && (!IsGamepadMode || gamepadInput.IsReady);
    }

    private bool IsGamepadMode => InputSource.SelectedIndex == 1;
    private bool IsStopping => activeCommand == FlightFlags.Stop && Environment.TickCount64 < commandExpiresAt;

    private void InputSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        // Первое событие приходит во время загрузки XAML, до создания Armed.
        if (Armed == null)
        {
            return;
        }

        Armed.IsChecked = false;
        gamepadButtons.Reset();
        ResetInput();
        RefreshInputAvailability();
        InputStatusText.Text = IsGamepadMode
            ? "Поиск геймпада…"
            : "Перетаскивайте стики мышью или пальцами.";
    }

    private void UpdateGamepadInput()
    {
        if (!IsGamepadMode)
        {
            Armed.IsEnabled = droneLink?.Responding == true && !IsStopping;
            InputStatusText.Text = "Перетаскивайте стики мышью или пальцами.";
            return;
        }

        var snapshot = gamepad.Read();
        Armed.IsEnabled = droneLink?.Responding == true && snapshot.HasValue && !IsStopping;
        if (snapshot is not { } state)
        {
            if (Armed.IsChecked == true)
            {
                Armed.IsChecked = false;
                Notice("Геймпад отключён. Управление выключено.");
            }

            ResetInput();
            gamepadButtons.Reset();
            InputStatusText.Text = gamepad.Error ?? "Геймпад не найден. Подключите его к Windows.";
            return;
        }

        LeftStick.Display(state.Left);
        RightStick.Display(state.Right);
        bool ready = Armed.IsChecked == true && IsActive &&
            gamepadInput.Update(state, TimeSpan.FromMilliseconds(Environment.TickCount64));
        var actions = gamepadButtons.Update(state, IsActive,
            canFly: ready && droneLink?.Responding == true && !IsStopping);
        ApplyGamepadActions(actions);

        if (Armed.IsChecked != true || !IsActive)
        {
            gamepadInput.Reset();
            flightInput.Release();
            InputStatusText.Text = state.Name + "\nСтики работают. Управление выключено.";
            return;
        }

        // Взлёт/посадка сбрасывают допуск: не восстанавливаем оси из старого снимка.
        ready = gamepadInput.IsReady;
        flightInput.SetLeft(ready ? state.Left : default);
        flightInput.SetRight(ready ? state.Right : default);
        FlightControls.IsEnabled = ready;
        InputStatusText.Text = state.Name + (ready
            ? "\nУправление стиками включено."
            : "\nОтпустите оба стика на 0,3 секунды.");
    }

    private void ApplyGamepadActions(GamepadActions actions)
    {
        if (actions.HasFlag(GamepadActions.Stop))
        {
            if (droneLink != null)
            {
                StopMotors();
            }
            return;
        }

        if (actions.HasFlag(GamepadActions.NextSpeed))
        {
            Power.Value = FlightInput.NextSpeed(Power.Value);
        }

        if (actions.HasFlag(GamepadActions.Land))
        {
            StartFlightAction(FlightFlags.Land);
        }
        else if (actions.HasFlag(GamepadActions.Takeoff))
        {
            StartFlightAction(FlightFlags.Takeoff);
        }
    }

    private void ResetInput()
    {
        gamepadInput.Reset();
        LeftStick?.ResetInput();
        RightStick?.ResetInput();
        flightInput.Release();
    }

    private void ArmChanged(object sender, RoutedEventArgs e)
    {
        if (Armed.IsChecked == true && IsStopping)
        {
            Armed.IsChecked = false;
            return;
        }

        ResetInput();
        activeCommand = IsStopping ? FlightFlags.Stop : FlightFlags.None;
        if (FlightControls != null)
        {
            RefreshInputAvailability();
        }

        if (Armed.IsChecked != true)
        {
            shouldSendNeutral = droneLink?.HasControlActivity == true;
        }
    }

    private void FocusLost(object? sender, EventArgs e)
    {
        // ArmChanged очищает команду; остановку сохраняем даже при смене фокуса.
        bool stopping = IsStopping;
        Armed.IsChecked = false;
        gamepadButtons.Reset();
        ResetInput();
        activeCommand = stopping ? FlightFlags.Stop : FlightFlags.None;

        shouldSendNeutral = droneLink?.HasControlActivity == true;
    }

    private void ActionClicked(object sender, RoutedEventArgs e) =>
        StartFlightAction((FlightFlags)byte.Parse((string)((Button)sender).Tag));

    private void StartFlightAction(FlightFlags command)
    {
        if (droneLink?.Responding != true || Armed.IsChecked != true || IsStopping ||
            (IsGamepadMode && !gamepadInput.IsReady))
        {
            return;
        }

        ResetInput();
        RefreshInputAvailability();
        activeCommand = command;
        commandExpiresAt = Environment.TickCount64 + 1000;
        Notice(activeCommand == FlightFlags.Takeoff ? "Отправляется команда взлёта." : "Отправляется команда посадки.");
    }

    private void EmergencyClicked(object sender, RoutedEventArgs e) => StopMotors();

    private void StopMotors()
    {
        ResetInput();
        Armed.IsChecked = false;
        activeCommand = FlightFlags.Stop;
        commandExpiresAt = Environment.TickCount64 + 1000;
        Armed.IsEnabled = false;
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
            gamepadButtons.Reset();
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
                    if (connectionToClose.HasControlActivity)
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
        gamepad.Dispose();
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
