#if NETFRAMEWORK
using System;
using BeatRelay.Diagnostics;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.MenuButtons;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BeatRelay.ModMenu;

internal sealed class OverlayCustomizerMenuManager : MonoBehaviour
{
    private static readonly System.Reflection.PropertyInfo? MenuButtonsCollectionProperty =
        typeof(MenuButtons).GetProperty(
            "Buttons",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private Plugin? plugin;
    private IOverlayLogger? logger;
    private MenuButton? menuButton;
    private OverlayCustomizerFlowCoordinator? flowCoordinator;
    private bool registered;
    private bool customizerOpen;
    private float nextRegisterAttemptTime;
    private float earliestRegisterTime;
    private float nextRegisterVerificationTime;

    public void Initialize(Plugin nextPlugin, IOverlayLogger nextLogger)
    {
        plugin = nextPlugin ?? throw new ArgumentNullException(nameof(nextPlugin));
        logger = nextLogger ?? throw new ArgumentNullException(nameof(nextLogger));
        earliestRegisterTime = Time.realtimeSinceStartup + 4f;
    }

    private void Update()
    {
        if (plugin == null)
        {
            return;
        }

        if (customizerOpen)
        {
            plugin.RuntimeCoordinator?.ApplyPendingCustomizationPreview();
        }

        var inMenuScene = IsLikelyMenuScene(SceneManager.GetActiveScene().name);
        if (!inMenuScene)
        {
            if (customizerOpen)
            {
                customizerOpen = false;
                plugin.RuntimeCoordinator?.SetCustomizationPreviewVisible(false);
            }

            return;
        }

        if (!registered)
        {
            TryRegister();
            return;
        }

        if (Time.realtimeSinceStartup >= nextRegisterVerificationTime)
        {
            VerifyRegistration();
        }
    }

    public void Shutdown()
    {
        TryUnregister();
        plugin = null;
        logger = null;
        menuButton = null;
        flowCoordinator = null;
    }

    private void OpenCustomizer()
    {
        if (plugin == null || !BsmlMenuReadiness.IsReady())
        {
            return;
        }

        try
        {
            menuButton ??= new MenuButton("BeatRelay", "Customize BeatRelay", OpenCustomizer);
            flowCoordinator = BeatSaberUI.CreateFlowCoordinator<OverlayCustomizerFlowCoordinator>();
            flowCoordinator.DidClose -= HandleCustomizerClosed;
            flowCoordinator.DidClose += HandleCustomizerClosed;
            flowCoordinator.Initialize(plugin);
            customizerOpen = true;
            BeatSaberUI.MainFlowCoordinator.PresentFlowCoordinator(flowCoordinator);
        }
        catch (Exception ex)
        {
            customizerOpen = false;
            plugin.RuntimeCoordinator?.SetCustomizationPreviewVisible(false);
            flowCoordinator = null;
            logger?.Warn("customizer_open_failed", ex.ToString());
        }
    }

    private void TryRegister()
    {
        if (Time.realtimeSinceStartup < earliestRegisterTime || Time.realtimeSinceStartup < nextRegisterAttemptTime)
        {
            return;
        }

        // Do not run readiness work every frame while BSML is still starting.
        nextRegisterAttemptTime = Time.realtimeSinceStartup + 2f;
        var started = plugin?.Config.DebugLogging == true
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L;
        if (!BsmlMenuReadiness.IsReady())
        {
            LogRegistrationTiming(started, "not_ready");
            return;
        }

        try
        {
            menuButton ??= new MenuButton("BeatRelay", "Customize BeatRelay", OpenCustomizer);
            MenuButtons.Instance.RegisterButton(menuButton);
            registered = true;
            nextRegisterVerificationTime = Time.realtimeSinceStartup + 5f;
            LogRegistrationTiming(started, "registered");
        }
        catch (InvalidOperationException)
        {
            nextRegisterAttemptTime = Time.realtimeSinceStartup + 2f;
            LogRegistrationTiming(started, "singleton_unavailable");
        }
        catch (ArgumentException)
        {
            registered = true;
            nextRegisterVerificationTime = Time.realtimeSinceStartup + 5f;
            LogRegistrationTiming(started, "already_registered");
        }
        catch (Exception ex)
        {
            logger?.Warn("customizer_menu_register_failed", ex.Message);
            nextRegisterAttemptTime = Time.realtimeSinceStartup + 2f;
            LogRegistrationTiming(started, "failed");
        }
    }

    private void VerifyRegistration()
    {
        nextRegisterVerificationTime = Time.realtimeSinceStartup + 5f;
        try
        {
            if (BsmlMenuReadiness.IsReady() && IsMenuButtonRegistered())
            {
                return;
            }

            registered = false;
            nextRegisterAttemptTime = 0f;
            TryRegister();
        }
        catch (InvalidOperationException)
        {
            registered = false;
            nextRegisterAttemptTime = Time.realtimeSinceStartup + 2f;
        }
        catch (Exception ex)
        {
            registered = false;
            nextRegisterAttemptTime = Time.realtimeSinceStartup + 2f;
            logger?.Warn("customizer_menu_verify_failed", ex.Message);
        }
    }

    private bool IsMenuButtonRegistered()
    {
        if (menuButton == null || MenuButtonsCollectionProperty == null)
        {
            return false;
        }

        var buttons = MenuButtonsCollectionProperty.GetValue(MenuButtons.Instance) as System.Collections.IList;
        return buttons?.Contains(menuButton) == true;
    }

    private void LogRegistrationTiming(long startedTimestamp, string result)
    {
        if (plugin?.Config.DebugLogging != true)
        {
            return;
        }

        var elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startedTimestamp;
        var elapsedMilliseconds = elapsedTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
        logger?.Info("performance_menu_registration", $"result={result}; durationMs={elapsedMilliseconds:0.###}");
    }

    private void TryUnregister()
    {
        if (!registered || menuButton == null)
        {
            return;
        }

        if (!BsmlMenuReadiness.IsReady())
        {
            registered = false;
            return;
        }

        try
        {
            MenuButtons.Instance.UnregisterButton(menuButton);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception ex)
        {
            logger?.Warn("customizer_menu_unregister_failed", ex.Message);
        }

        registered = false;
    }

    private void HandleCustomizerClosed()
    {
        customizerOpen = false;
        if (flowCoordinator != null)
        {
            flowCoordinator.DidClose -= HandleCustomizerClosed;
        }

        flowCoordinator = null;
        plugin?.RuntimeCoordinator?.SetCustomizationPreviewVisible(false);
    }

    private static bool IsLikelyMenuScene(string sceneName)
    {
        return !string.IsNullOrWhiteSpace(sceneName)
            && sceneName.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
#endif
