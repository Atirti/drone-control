using SDL3;

namespace DroneControl;

/// <summary>
/// Ввод через SDL3-CS: раскладку устройств определяет SDL. Использовать на UI-потоке.
/// Класс не открывает сетевые соединения и не отправляет полётные команды.
/// </summary>
public sealed class SdlGamepad : IDisposable
{
    private nint handle;
    private uint deviceId;
    private string deviceName = "Геймпад";
    private bool initialized;
    private bool disposed;
    private long nextScanAt;

    public string? Error { get; private set; }

    public GamepadState? Read()
    {
        if (disposed || Error != null || !EnsureInitialized())
        {
            return null;
        }

        // WPF владеет циклом сообщений. SDL-события отключены, состояние обновляем вручную.
        SDL.UpdateGamepads();
        if (handle != nint.Zero && !SDL.GamepadConnected(handle))
        {
            CloseDevice();
            nextScanAt = Environment.TickCount64 + 500;
            return null; // Сначала UI должен увидеть отключение и снять разрешение управления.
        }

        if (handle == nint.Zero)
        {
            if (Environment.TickCount64 < nextScanAt)
            {
                return null;
            }

            nextScanAt = Environment.TickCount64 + 1000;
            foreach (uint candidate in SDL.GetGamepads(out _) ?? [])
            {
                var opened = SDL.OpenGamepad(candidate);
                if (opened == nint.Zero)
                {
                    continue;
                }

                // Неполная раскладка не должна превращаться в неподвижные «исправные» оси.
                if (!SDL.GamepadHasAxis(opened, SDL.GamepadAxis.LeftX) ||
                    !SDL.GamepadHasAxis(opened, SDL.GamepadAxis.LeftY) ||
                    !SDL.GamepadHasAxis(opened, SDL.GamepadAxis.RightX) ||
                    !SDL.GamepadHasAxis(opened, SDL.GamepadAxis.RightY))
                {
                    SDL.CloseGamepad(opened);
                    continue;
                }

                handle = opened;
                deviceId = candidate;
                deviceName = SDL.GetGamepadName(handle) ?? "Геймпад";
                SDL.UpdateGamepads();
                break;
            }
        }

        if (handle == nint.Zero)
        {
            return null;
        }

        var left = GamepadInput.Normalize(
            SDL.GetGamepadAxis(handle, SDL.GamepadAxis.LeftX),
            SDL.GetGamepadAxis(handle, SDL.GamepadAxis.LeftY));
        var right = GamepadInput.Normalize(
            SDL.GetGamepadAxis(handle, SDL.GamepadAxis.RightX),
            SDL.GetGamepadAxis(handle, SDL.GamepadAxis.RightY));
        return new GamepadState(deviceId, deviceName, left, right);
    }

    private bool EnsureInitialized()
    {
        if (initialized)
        {
            return true;
        }

        try
        {
            // У WPF нет SDL-окна. Фокус и разрешение полёта контролирует MainWindow.
            SDL.SetHint(SDL.Hints.JoystickAllowBackgroundEvents, "1");
            if (!SDL.InitSubSystem(SDL.InitFlags.Gamepad))
            {
                Error = "Не удалось запустить SDL3: " + SDL.GetError();
                return false;
            }

            initialized = true;
            SDL.SetGamepadEventsEnabled(false);
            SDL.SetJoystickEventsEnabled(false);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Error = "Не удалось загрузить библиотеку SDL3: " + ex.Message;
            return false;
        }
    }

    private void CloseDevice()
    {
        if (handle != nint.Zero)
        {
            SDL.CloseGamepad(handle);
            handle = nint.Zero;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CloseDevice();
        if (initialized)
        {
            SDL.QuitSubSystem(SDL.InitFlags.Gamepad);
            initialized = false;
        }
    }
}
