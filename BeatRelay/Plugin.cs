using System;
using System.IO;
using System.Reflection;
#if NETFRAMEWORK
using IPA;
using IPA.Logging;
using UnityEngine;
#endif
using BeatRelay.BeatSaber;
using BeatRelay.Config;
using BeatRelay.Diagnostics;
#if NETFRAMEWORK
using BeatRelay.ModMenu;
#endif

namespace BeatRelay;

#if NETFRAMEWORK
[Plugin(RuntimeOptions.SingleStartInit)]
#endif
public sealed class Plugin
{
    public const string Name = "BeatRelay";
    public const string Version = "0.1.0";

    private static Plugin? instance;

    private ConfigManager? configManager;
    private IOverlayLogger? logger;
#if NETFRAMEWORK
    private BeatSaberSessionLogger? sessionLogger;
    private BeatSaberRuntimeCoordinator? runtimeCoordinator;
    private OverlayCustomizerMenuManager? overlayCustomizerMenuManager;
    private GameObject? overlayCustomizerMenuManagerObject;
#endif

    private OverlayConfig? config;

#if NETFRAMEWORK
    public Plugin()
        : this(null, null)
    {
    }
#else
    public Plugin()
        : this(null, null)
    {
    }
#endif

    public Plugin(ConfigManager? configManager, IOverlayLogger? logger)
    {
        this.configManager = configManager;
        this.logger = logger;
        instance = this;
        WriteBootstrapTrace("base_ctor");
    }

    public static Plugin? Instance => instance;

    public bool IsStarted { get; private set; }

    public OverlayConfig Config => config ?? throw new InvalidOperationException("Plugin has not started.");

#if NETFRAMEWORK
    internal BeatSaberSessionLogger? SessionLogger => sessionLogger;

    internal BeatSaberRuntimeCoordinator? RuntimeCoordinator => runtimeCoordinator;

    internal ConfigManager? ConfigManager => configManager;
#endif

#if NETFRAMEWORK
    [Init]
    public void Init(IPA.Logging.Logger ipaLogger)
    {
        logger ??= new BsipaOverlayLogger(ipaLogger);
        WriteBootstrapTrace("init_method_enter");
    }

    [OnStart]
#endif
    public void OnApplicationStart()
    {
        WriteBootstrapTrace("on_start_enter");
        try
        {
            logger ??= CreateDefaultLogger();
            configManager ??= CreateDefaultConfigManager();
            config = configManager.LoadOrCreate();
            logger = new ConfigurableOverlayLogger(logger, config.DebugLogging);
            logger.Info("plugin_init", $"{Name} {Version} init received from BSIPA.");
            IsStarted = true;

            logger.Info(
                "plugin_startup",
                $"{Name} {Version} started. Enabled={config.Enabled}; UpdateIntervalSeconds={config.UpdateIntervalSeconds}; Desktop={config.EnableDesktopOverlay}.");
#if NETFRAMEWORK
            runtimeCoordinator = new BeatSaberRuntimeCoordinator(config, logger);
            overlayCustomizerMenuManagerObject = new GameObject("BeatRelay.CustomizerMenuManager");
            UnityEngine.Object.DontDestroyOnLoad(overlayCustomizerMenuManagerObject);
            overlayCustomizerMenuManager = overlayCustomizerMenuManagerObject.AddComponent<OverlayCustomizerMenuManager>();
            overlayCustomizerMenuManager.Initialize(this, logger);
            sessionLogger = new BeatSaberSessionLogger(logger);
            sessionLogger.Install();
#endif
            WriteBootstrapTrace("on_start_exit");
        }
        catch (Exception ex)
        {
            WriteBootstrapTrace("on_start_exception");
            WriteBootstrapTrace(ex.ToString());
            throw;
        }
    }

#if NETFRAMEWORK
    [OnExit]
#endif
    public void OnApplicationQuit()
    {
        WriteBootstrapTrace("on_exit_enter");
        if (!IsStarted)
        {
            return;
        }

        logger?.Info("plugin_shutdown", $"{Name} stopped.");
#if NETFRAMEWORK
        overlayCustomizerMenuManager?.Shutdown();
        overlayCustomizerMenuManager = null;
        if (overlayCustomizerMenuManagerObject != null)
        {
            UnityEngine.Object.Destroy(overlayCustomizerMenuManagerObject);
            overlayCustomizerMenuManagerObject = null;
        }

        sessionLogger?.Uninstall();
        sessionLogger = null;
        runtimeCoordinator?.Dispose();
        runtimeCoordinator = null;
#endif
        IsStarted = false;
        WriteBootstrapTrace("on_exit_after_shutdown");
    }

    private static ConfigManager CreateDefaultConfigManager()
    {
        var userData = Path.Combine(GetBeatSaberRootDirectory(), "UserData");
        return new ConfigManager(userData);
    }

    private static IOverlayLogger CreateDefaultLogger()
    {
#if NETFRAMEWORK
        throw new InvalidOperationException("BSIPA must initialize the plugin logger before startup.");
#else
        var logs = Path.Combine(GetBeatSaberRootDirectory(), "Logs");
        return new OverlayLogger(logs);
#endif
    }

    private static string GetBeatSaberRootDirectory()
    {
#if NETFRAMEWORK
        var pluginPath = Assembly.GetExecutingAssembly().Location;
        var pluginDirectory = Path.GetDirectoryName(pluginPath);
        if (!string.IsNullOrWhiteSpace(pluginDirectory))
        {
            var directory = new DirectoryInfo(pluginDirectory);
            return directory.Parent?.FullName ?? directory.FullName;
        }

        return Environment.CurrentDirectory;
#else
        return AppContext.BaseDirectory;
#endif
    }

    private static void WriteBootstrapTrace(string stage)
    {
        // Bootstrap tracing is disabled; runtime diagnostics use the configured logger.
    }

}
