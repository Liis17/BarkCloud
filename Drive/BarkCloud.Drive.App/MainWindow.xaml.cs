using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using BarkCloud.Drive.Contracts;
using BarkCloud.Drive.Contracts.Localization;

using StreamJsonRpc;

using Wpf.Ui.Controls;

namespace BarkCloud.Drive.App;

public partial class MainWindow : FluentWindow
{
    private readonly SemaphoreSlim _engineOperation = new(1, 1);
    private IDriveEngine? _engine;
    private readonly DispatcherTimer _statusTimer;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly bool _startHidden;
    private bool _reallyExit;
    private bool _avatarLoaded;
    private CancellationTokenSource _engineOperationCancellation = new();
    private Task<EngineStatus>? _statusRequest;
    private Task<IDriveEngine?>? _restartTask;
    private bool _pollingPaused;

    public MainWindow(bool startHidden = false)
    {
        _startHidden = startHidden;
        InitializeComponent();

        // Иконка трея — кадр 32px встроенного .ico (кадр выбирается по размеру).
        using var icoStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/barkcloud.ico")).Stream;
        using var appIcon = new System.Drawing.Icon(icoStream, new System.Drawing.Size(32, 32));
        var iconSource = Imaging.CreateBitmapSourceFromHIcon(appIcon.Handle,
            Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        iconSource.Freeze();
        TrayIcon.Icon = iconSource;

        ContentRendered += OnContentRendered;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();

        // В трее обновляем реже, чтобы отслеживать синхронизацию без лишних запросов.
        StateChanged += (_, _) => UpdatePolling();          // свернуть/развернуть
        IsVisibleChanged += (_, _) => UpdatePolling();      // показать/скрыть в трей
    }

    // Текущий прокси движка — для модалки настроек.
    public IDriveEngine? Engine => _engine;

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        if (!TrayIcon.IsRegistered)
            TrayIcon.Register();

        await InitializeAsync();

        if (_startHidden)
        {
            Hide(); // автозагрузка: ушли в трей, диск уже монтируется
            WindowState = WindowState.Normal; // следующее открытие из трея — нормальное
        }
    }

    private async Task InitializeAsync()
    {
        var engine = await EnsureEngineAsync();
        if (engine == null)
        {
            UpdatePolling();
            return; // баннер «движок не запущен» показан
        }

        await ProceedAfterEngineAsync();
    }

    // Общий путь после подключения к движку: первый запуск → мастер; иначе статус + автомонтаж.
    private async Task ProceedAfterEngineAsync()
    {
        if (!_settings.Configured)
        {
            var wizardResult = await RunWizardAsync();
            if (wizardResult == false)
            {
                ExitApp(); // первичная настройка отменена — без неё пользоваться нечем
                return;
            }

            // В мастере могли сменить адрес сервера → движок перезапущен, прокси обновлён.
            if (wizardResult != true || _engine == null)
            {
                UpdateEngineBanner();
                UpdatePolling();
                return;
            }
        }

        var status = await PollStatusAsync(waitForOperation: true);
        if (status is { Authenticated: true, Mounted: false } && _engine is { } activeEngine)
            await AutoMountAsync(activeEngine);

        UpdatePolling();
    }

    private async Task<IDriveEngine?> EnsureEngineAsync()
    {
        await _engineOperation.WaitAsync();
        try
        {
            return _reallyExit ? null : _engine ?? await ConnectEngineCoreAsync();
        }
        finally
        {
            _engineOperation.Release();
        }
    }

    private async Task<IDriveEngine?> ConnectEngineCoreAsync(bool propagateTimeout = false)
    {
        if (_reallyExit)
            return null;

        if (_engineOperationCancellation.IsCancellationRequested)
        {
            _engineOperationCancellation.Dispose();
            _engineOperationCancellation = new CancellationTokenSource();
        }

        try
        {
            _engine = await EngineLauncher.ConnectAsync(_engineOperationCancellation.Token);
            await _engine.SetLanguageAsync(Loc.CurrentCode)
                .WaitAsync(TimeSpan.FromSeconds(10), _engineOperationCancellation.Token); // движок мог стартовать с другой культурой
        }
        catch (OperationCanceledException) when (_reallyExit || _engineOperationCancellation.IsCancellationRequested)
        {
            DisposeEngineProxy();
            return null;
        }
        catch (TimeoutException) when (propagateTimeout)
        {
            DisposeEngineProxy();
            throw;
        }
        catch (Exception ex)
        {
            DisposeEngineProxy();
            StatusText.Text = Loc.T("Main_EngineUnavailableFmt", ex.Message);
        }

        UpdateEngineBanner();
        return _engine;
    }

    // Перезапуск движка по запросу из настроек: корректное завершение + добивание процесса + реконнект.
    public Task<IDriveEngine?> RestartEngineAsync()
    {
        if (_restartTask is { IsCompleted: false })
            return _restartTask;

        return _restartTask = RestartEngineCoreAsync();
    }

    private async Task<IDriveEngine?> RestartEngineCoreAsync()
    {
        if (_reallyExit)
            return null;

        if (!await _engineOperation.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            // Прерываем зависший опрос и его возможное переподключение.
            _engineOperationCancellation.Cancel();
            DisposeEngineProxy();
            if (!await _engineOperation.WaitAsync(TimeSpan.FromSeconds(1)))
            {
                UpdateEngineBanner();
                return null;
            }
        }

        try
        {
            if (_reallyExit)
                return null;

            _engineOperationCancellation.Dispose();
            _engineOperationCancellation = new CancellationTokenSource();
            var engine = _engine ?? await EngineLauncher.TryConnectToRunningEngineAsync();
            _engine = engine;
            var shutdownCompleted = false;

            if (engine != null)
            {
                StatusText.Text = Loc.T("Main_EngineShuttingDown");
                try
                {
                    await engine.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    shutdownCompleted = true;
                }
                catch { /* возможно завис */ }
                finally { DisposeEngineProxy(); }
            }

            if (shutdownCompleted)
                await EngineLauncher.WaitForEngineExitAsync();
            if (_reallyExit)
                return null;
            await EngineLauncher.KillEngineAsync();
            return await ConnectEngineCoreAsync();
        }
        finally
        {
            _engineOperation.Release();
        }
    }

    private async Task<bool?> RunWizardAsync()
    {
        var wasPaused = _pollingPaused;
        _pollingPaused = true;
        _statusTimer.Stop();
        try
        {
            // Остановка таймера не отменяет уже начатый опрос: ждём его до открытия мастера.
            if (!await _engineOperation.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                StatusText.Text = Loc.T("Main_EngineUnavailableFmt", new TimeoutException().Message);
                return null;
            }
            _engineOperation.Release();
            if (_reallyExit || _engine == null)
                return null;

            var wizard = new FirstRunWizard(_engine, _settings, RestartEngineAsync) { Owner = this };
            return wizard.ShowDialog() == true;
        }
        finally
        {
            _pollingPaused = wasPaused;
        }
    }

    private async Task AutoMountAsync(IDriveEngine engine)
    {
        var letter = PreferredLetter();
        if (letter == null)
        {
            StatusText.Text = Loc.T("Main_NoFreeLetter");
            return;
        }

        try
        {
            var status = await engine.MountAsync(letter, _settings.DriveName);
            if (status.Mounted)
            {
                _settings.DriveLetter = letter;
                _settings.Save();
            }

            Apply(status);
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Main_MountErrorFmt", ex.Message);
        }
    }

    // Запомненная буква (если ещё свободна), иначе первая свободная.
    private string? PreferredLetter()
    {
        var free = DriveLetters.Free();
        if (_settings.DriveLetter is { } saved && free.Contains(saved))
            return saved;

        return free.FirstOrDefault();
    }

    private void Apply(EngineStatus s)
    {
        UpdateTrayStatus(s);

        UsernameText.Text = !string.IsNullOrEmpty(s.Username)
            ? s.Username
            : Loc.T(s.Authenticated ? "User_LoggedIn" : "User_NotLoggedIn");
        ServerText.Text = string.IsNullOrEmpty(s.ServerHost) ? string.Empty : Loc.T("Main_ServerFmt", s.ServerHost);

        if (s.Authenticated)
        {
            if (!_avatarLoaded)
            {
                _avatarLoaded = true; // одна попытка загрузки на сессию
                _ = LoadAvatarAsync();
            }
        }
        else
        {
            _avatarLoaded = false;
            AvatarEllipse.Visibility = Visibility.Collapsed;
        }

        if (s.DiskTotalBytes > 0)
        {
            var other = Math.Max(0, s.DiskOtherBytes);
            var s3 = Math.Max(0, s.DiskS3Bytes);
            var free = Math.Max(0, s.DiskTotalBytes - other - s3);

            UsageOtherCol.Width = new GridLength(other, GridUnitType.Star);
            UsageS3Col.Width = new GridLength(s3, GridUnitType.Star);
            UsageFreeCol.Width = new GridLength(free, GridUnitType.Star);

            UsageText.Text = Loc.T("Main_StorageDiskUsageFmt", Bytes(other + s3), Bytes(s.DiskTotalBytes));
            LegendOtherText.Text = $"{Loc.T("Main_StorageOther")} — {Bytes(other)}";
            LegendS3Text.Text = $"{Loc.T("Main_StorageCloud")} — {Bytes(s3)}";
            LegendFreeText.Text = $"{Loc.T("Main_StorageFree")} — {Bytes(free)}";
            UsageLegend.Visibility = Visibility.Visible;
        }
        else
        {
            UsageOtherCol.Width = new GridLength(0, GridUnitType.Star);
            UsageS3Col.Width = new GridLength(0, GridUnitType.Star);
            UsageFreeCol.Width = new GridLength(1, GridUnitType.Star);
            UsageText.Text = "—";
            UsageLegend.Visibility = Visibility.Collapsed;
        }

        DriveStateText.Text = s.Mounted
            ? Loc.T("Main_DriveMountedFmt", s.DriveLetter ?? string.Empty, s.VolumeLabel ?? string.Empty)
            : Loc.T("Main_DriveNotMounted");

        StatusText.Text = !string.IsNullOrEmpty(s.Error)
            ? Loc.T("Common_ErrorFmt", s.Error)
            : !string.IsNullOrEmpty(s.LastSyncError)
                ? Loc.T("Main_SyncFmt", s.LastSyncError)
                : (s.Message ?? string.Empty);
    }

    private async Task LoadAvatarAsync()
    {
        if (_engine == null)
            return;

        try
        {
            var bytes = await _engine.GetAvatarAsync();
            if (bytes is not { Length: > 0 })
                return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();

            AvatarBrush.ImageSource = bmp;
            AvatarEllipse.Visibility = Visibility.Visible;
        }
        catch
        {
            // аватар не критичен — оставляем без него
        }
    }

    private async Task<EngineStatus?> PollStatusAsync(bool waitForOperation = false)
    {
        if (_reallyExit || (_pollingPaused && !waitForOperation))
            return null;
        if (!await _engineOperation.WaitAsync(waitForOperation ? TimeSpan.FromSeconds(10) : TimeSpan.Zero))
        {
            if (waitForOperation)
                StatusText.Text = Loc.T("Main_EngineUnavailableFmt", new TimeoutException().Message);
            return null;
        }

        EngineStatus? status;
        try
        {
            var engine = _engine;
            if (_reallyExit || engine == null)
            {
                UpdateEngineBanner();
                return null;
            }

            try
            {
                status = await GetStatusWithTimeoutAsync(engine);
                if (status == null)
                    return null;
            }
            catch (OperationCanceledException) when (_reallyExit || _engineOperationCancellation.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (IsEngineTransportFailure(ex))
            {
                DisposeEngineProxy();
                if (_reallyExit || _engineOperationCancellation.IsCancellationRequested)
                    return null;

                engine = await ConnectEngineCoreAsync();
                if (engine == null)
                    return null;

                try
                {
                    status = await GetStatusWithTimeoutAsync(engine);
                    if (status == null)
                        return null;
                }
                catch (OperationCanceledException) when (_reallyExit || _engineOperationCancellation.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception retryException) when (IsEngineTransportFailure(retryException))
                {
                    DisposeEngineProxy();
                    StatusText.Text = Loc.T("Main_EngineUnavailableFmt", retryException.Message);
                    UpdateEngineBanner();
                    return null;
                }
                catch (Exception retryException)
                {
                    StatusText.Text = Loc.T("Common_ErrorFmt", retryException.Message);
                    UpdateEngineBanner();
                    return null;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = Loc.T("Common_ErrorFmt", ex.Message);
                UpdateEngineBanner();
                return null;
            }
        }
        finally
        {
            _engineOperation.Release();
        }

        if (!_reallyExit)
            Apply(status);
        return status;
    }

    private async Task<EngineStatus?> GetStatusWithTimeoutAsync(IDriveEngine engine)
    {
        if (_statusRequest != null)
        {
            ShowEngineNotResponding();
            return null;
        }

        _statusRequest = null;
        var request = engine.GetStatusAsync();
        _statusRequest = request;
        _ = ObserveStatusRequestAsync(request);
        var timedOut = false;

        try
        {
            return await request.WaitAsync(TimeSpan.FromSeconds(10), _engineOperationCancellation.Token);
        }
        catch (TimeoutException) when (!request.IsCompleted)
        {
            timedOut = true;
            ShowEngineNotResponding();
            _ = ApplyLateStatusAsync(engine, request);
            return null;
        }
        finally
        {
            if (!timedOut && request.IsCompleted && ReferenceEquals(_statusRequest, request))
                _statusRequest = null;
        }
    }

    private async Task ApplyLateStatusAsync(IDriveEngine engine, Task<EngineStatus> request)
    {
        try
        {
            // Продолжение возвращается на UI-контекст; до Apply новый опрос не запускается.
            var status = await request;
            if (!_reallyExit && ReferenceEquals(_engine, engine))
                Apply(status);
        }
        catch { /* Наблюдаем ошибку IPC-вызова после таймаута ожидания. */ }
        finally
        {
            if (ReferenceEquals(_statusRequest, request))
                _statusRequest = null;
        }
    }

    private static async Task ObserveStatusRequestAsync(Task request)
    {
        try { await request; }
        catch { /* IPC-вызов может завершиться ошибкой уже после таймаута ожидания. */ }
    }

    private void ShowEngineNotResponding()
    {
        StatusText.Text = Loc.T("Main_EngineNotResponding");
        TrayIcon.TooltipText = Loc.T("Main_EngineNotResponding");
        UpdateEngineBanner();
    }

    private static bool IsEngineTransportFailure(Exception exception)
        => exception is ConnectionLostException or ObjectDisposedException or IOException;

    private void UpdatePolling()
    {
        _statusTimer.Stop();
        if (_reallyExit || _pollingPaused)
            return;

        var visible = IsVisible && WindowState != WindowState.Minimized;
        _statusTimer.Interval = TimeSpan.FromSeconds(visible ? 5 : 30);
        _statusTimer.Start();
        if (visible && _engine != null)
            _ = PollStatusAsync(); // мгновенное обновление при возврате
    }

    private void UpdateEngineBanner()
    {
        EngineWarning.Visibility = _engine == null ? Visibility.Visible : Visibility.Collapsed;
        if (_engine == null)
        {
            TrayIcon.TooltipText = Loc.T("Main_EngineWarningTitle");
            PendingUploadsMenu.Visibility = Visibility.Collapsed;
        }
    }

    private async void StartEngineClick(object sender, RoutedEventArgs e)
    {
        await _engineOperation.WaitAsync();
        IDriveEngine? engine;
        try
        {
            if (_reallyExit)
                return;
            DisposeEngineProxy();
            try
            {
                engine = await ConnectEngineCoreAsync(propagateTimeout: true);
            }
            catch (TimeoutException)
            {
                if (_reallyExit)
                    return;
                await EngineLauncher.KillEngineAsync();
                engine = await ConnectEngineCoreAsync();
            }
        }
        finally
        {
            _engineOperation.Release();
        }

        if (engine != null)
            await ProceedAfterEngineAsync();
    }

    private async void OpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_engine == null)
        {
            StatusText.Text = Loc.T("Common_EngineNotRunning");
            return;
        }

        _pollingPaused = true;
        _statusTimer.Stop(); // пауза опроса на время модалки
        try
        {
            if (!await _engineOperation.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                StatusText.Text = Loc.T("Main_EngineUnavailableFmt", new TimeoutException().Message);
                return;
            }
            _engineOperation.Release();
            if (_reallyExit || _engine == null)
                return;

            new SettingsWindow(this, _settings) { Owner = this }.ShowDialog();
        }
        finally
        {
            _pollingPaused = false;
            UpdatePolling();
        }

        // Возможно, в настройках вышли из аккаунта (Configured=false) → мастер заново.
        if (!_settings.Configured && _engine != null && await RunWizardAsync() == false)
        {
            ExitApp();
            return;
        }

        UpdatePolling();
    }

    // ───────── трей ─────────

    private void ShowClick(object sender, RoutedEventArgs e) => ShowFromTray();

    private void OpenOutboxClick(object sender, RoutedEventArgs e)
    {
        try
        {
            OutboxFolder.Open();
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Settings_OutboxOpenFailedFmt", ex.Message);
            ShowFromTray();
        }
    }

    private async void MountClick(object sender, RoutedEventArgs e)
    {
        var engine = await EnsureEngineAsync();
        if (engine != null)
            await AutoMountAsync(engine);
    }

    private async void UnmountClick(object sender, RoutedEventArgs e)
    {
        if (_engine == null)
            return;

        try
        {
            Apply(await _engine.UnmountAsync());
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.T("Common_ErrorFmt", ex.Message);
        }
    }

    private async void ExitClick(object sender, RoutedEventArgs e)
    {
        _reallyExit = true;
        _statusTimer.Stop();
        if (_restartTask is { IsCompleted: false })
        {
            // Перезапуск уже завершает движок: не прерываем его Shutdown и не отправляем второй.
            TrayIcon.Unregister();
            Application.Current.Shutdown();
            return;
        }

        var acquired = await _engineOperation.WaitAsync(TimeSpan.FromMilliseconds(500));
        if (!acquired)
        {
            _engineOperationCancellation.Cancel();
            DisposeEngineProxy();
        }
        try
        {
            if (!acquired)
            {
                try
                {
                    var engine = await EngineLauncher.TryConnectToRunningEngineAsync();
                    if (engine != null)
                    {
                        try { await engine.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                        catch { /* движок завершается */ }
                        finally { (engine as IDisposable)?.Dispose(); }
                    }
                }
                catch { /* движок уже недоступен */ }
            }
            else if (_engine != null)
            {
                try { await _engine.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch { /* движок завершается */ }
                finally { DisposeEngineProxy(); }
            }
        }
        finally
        {
            if (acquired)
                _engineOperation.Release();
        }

        TrayIcon.Unregister();
        Application.Current.Shutdown();
    }

    private void DisposeEngineProxy()
    {
        var engine = _engine;
        _engine = null;
        _statusRequest = null;
        (engine as IDisposable)?.Dispose();
        UpdateEngineBanner();
    }

    private void UpdateTrayStatus(EngineStatus status)
    {
        TrayIcon.TooltipText = !string.IsNullOrEmpty(status.LastSyncError)
            ? Loc.T("Main_SyncFmt", status.LastSyncError)
            : status.PendingUploads > 0
                ? Loc.T("Tray_PendingUploadsFmt", status.PendingUploads)
                : "BarkCloud Drive";
        PendingUploadsMenu.Visibility = status.PendingUploads > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // Закрытие окна (крестик) → сворачивание в трей; диск и движок продолжают работать.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyExit)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _statusTimer.Stop();
        _engineOperationCancellation.Cancel();
        DisposeEngineProxy();
        try { TrayIcon.Unregister(); } catch { /* ignore */ }
        Application.Current.Shutdown();
    }

    private static string Bytes(long b)
    {
        var units = Loc.T("Units_Bytes").Split(',');
        double v = b;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return $"{v:0.##} {units[i]}";
    }
}
