using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The wake-and-unlock feature (against a fake adb that logs its calls; never the real adb or a phone):
/// a phone whose last resumed activity is PhoneBox is woken (KEYCODE_WAKEUP) and swiped up; one showing
/// anything else is left alone; a screen that is already on and locked only gets the swipe. The wake phone
/// is chosen from the full attached list (independently of the panel phone), is never woken while the
/// computer is locked, and a screen whose state cannot be read is not touched.
/// </summary>
[Collection("adb session")] // SessionEndingTests flips the static session-ending window
public sealed class AdbWakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-adb-wake-" + Guid.NewGuid().ToString("N"));
    private string _adbPath = "";
    public AdbWakeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string CallLog => Path.Combine(_dir, "calls.log");

    /// <summary>
    /// <paramref name="device"/> attached; its screen is <paramref name="power"/> ("OFF" / "ON"), its last resumed
    /// activity is <paramref name="resumed"/>, and its current focus is <paramref name="focus"/> (an activity
    /// component, "null" for no focused window, "NotificationShade" for the component-less keyguard window). When
    /// <paramref name="overrideSize"/> is set, "wm size" reports it on top of the physical 1080x2400.
    /// </summary>
    private string FakeAdb(string power, string resumed, string focus, string device = "FAKE1", string? overrideSize = null)
    {
        var focusLine = focus == "null" ? "mCurrentFocus=null" : $"mCurrentFocus=Window{{abc u0 {focus}}}";
        if (OperatingSystem.IsWindows())
        {
            var size = overrideSize is null
                ? "echo Physical size: 1080x2400"
                : $"echo Physical size: 1080x2400& echo Override size: {overrideSize}";
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%1\"==\"devices\" (echo List of devices attached& echo " + device + "\tdevice& exit /b 0)",
                $"if \"%3\"==\"shell\" if \"%4\"==\"dumpsys\" if \"%5\"==\"power\" (echo Display Power: state={power}& exit /b 0)",
                $"if \"%3\"==\"shell\" if \"%4\"==\"dumpsys\" if \"%5\"==\"activity\" if \"%6\"==\"activities\" (echo   topResumedActivity=ActivityRecord{{123 t92 {resumed}}}& exit /b 0)",
                $"if \"%3\"==\"shell\" if \"%4\"==\"dumpsys\" if \"%5\"==\"window\" (echo   {focusLine}& exit /b 0)",
                $"if \"%3\"==\"shell\" if \"%4\"==\"wm\" if \"%5\"==\"size\" ({size}& exit /b 0)",
                "exit /b 0", ""));
            _adbPath = cmd;
            return cmd;
        }
        var sizeSh = overrideSize is null
            ? "echo 'Physical size: 1080x2400'"
            : $"echo 'Physical size: 1080x2400'; echo 'Override size: {overrideSize}'";
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            $"if [ \"$1\" = devices ]; then printf 'List of devices attached\\n{device}\\tdevice\\n'; exit 0; fi",
            $"if [ \"$3\" = shell ] && [ \"$4\" = dumpsys ] && [ \"$5\" = power ]; then echo 'Display Power: state={power}'; exit 0; fi",
            $"if [ \"$3\" = shell ] && [ \"$4\" = dumpsys ] && [ \"$5\" = activity ] && [ \"$6\" = activities ]; then echo '  topResumedActivity=ActivityRecord{{123 t92 {resumed}}}'; exit 0; fi",
            $"if [ \"$3\" = shell ] && [ \"$4\" = dumpsys ] && [ \"$5\" = window ]; then echo '  {focusLine}'; exit 0; fi",
            $"if [ \"$3\" = shell ] && [ \"$4\" = wm ] && [ \"$5\" = size ]; then {sizeSh}; exit 0; fi",
            "exit 0", ""));
        File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        _adbPath = sh;
        return sh;
    }

    private string[] Calls()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(CallLog)) return [];
                using var stream = new FileStream(CallLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            }
            catch (IOException) when (attempt < 50) { Thread.Sleep(20); }
        }
    }

    private AdbReverse Adb()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = FakeAdb(power: "OFF", resumed: "x", focus: "x"), adbWake = new { enabled = true, deviceSerial = "FAKE1" } }));
        var store = new ConfigStore(path);
        return new AdbReverse(store, NullLogger<AdbReverse>.Instance);
    }

    private Task Wake(AdbReverse adb)
    {
        var wake = typeof(AdbReverse).GetMethod("WakePhoneAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)wake.Invoke(adb, [_adbPath, "FAKE1", CancellationToken.None])!;
    }

    private Task Pass(AdbReverse adb)
    {
        var ensure = typeof(AdbReverse).GetMethod("Ensure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (Task)ensure.Invoke(adb, [CancellationToken.None])!;
    }

    [Fact]
    public async Task PhoneBoxOnASleepyPhoneIsWokenAndSwiped()
    {
        var adb = Adb();
        // the fake answers the wake round: screen OFF, PhoneBox last resumed, launcher focused (the keyguard will be)
        File.Delete(CallLog);
        FakeAdb(power: "OFF", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "com.android.launcher/.UxLauncher");
        var error = await Record.ExceptionAsync(() => Wake(adb));
        Assert.True(error is null, $"the wake failed: {error?.Message}");
        var calls = Calls();
        var key = calls.ToList().FindIndex(c => c == "-s FAKE1 shell input keyevent 224");
        var swipe = calls.ToList().FindIndex(c => c == "-s FAKE1 shell input swipe 540 2040 540 1200 400");
        Assert.True(key >= 0, "the wake key was not sent");
        Assert.True(swipe > key, $"the swipe was not sent after the wake key: {string.Join(" | ", calls)}");
    }

    [Fact]
    public async Task APhoneAlreadyOnAndLockedOnlyGetsTheSwipe()
    {
        var adb = Adb();
        File.Delete(CallLog);
        FakeAdb(power: "ON", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "com.android.systemui/.OneShotInterceptView");
        var error = await Record.ExceptionAsync(() => Wake(adb));
        Assert.True(error is null, $"the wake failed: {error?.Message}");
        var calls = Calls();
        Assert.DoesNotContain(calls, c => c.Contains("keyevent 224", StringComparison.Ordinal));
        Assert.Contains("-s FAKE1 shell input swipe 540 2040 540 1200 400", calls);
    }

    [Fact]
    public async Task PhoneBoxNotOnScreenIsLeftAlone()
    {
        var adb = Adb();
        File.Delete(CallLog);
        FakeAdb(power: "OFF", resumed: "com.android.launcher/.UxLauncher", focus: "com.android.launcher/.UxLauncher");
        var error = await Record.ExceptionAsync(() => Wake(adb));
        Assert.True(error is null, $"the wake failed: {error?.Message}");
        var calls = Calls();
        Assert.DoesNotContain(calls, c => c.Contains("keyevent 224", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("input swipe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneBoxAlreadyOpenGetsNothing()
    {
        var adb = Adb();
        File.Delete(CallLog);
        FakeAdb(power: "ON", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "net.oxog.phonebox.deskpanel/.MainActivity");
        var error = await Record.ExceptionAsync(() => Wake(adb));
        Assert.True(error is null, $"the wake failed: {error?.Message}");
        var calls = Calls();
        Assert.DoesNotContain(calls, c => c.Contains("keyevent 224", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("input swipe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSwipeCoordinatesComeFromTheScreenSize()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = FakeAdb(power: "OFF", resumed: "x", focus: "x") }));
        var store = new ConfigStore(path);
        File.Delete(CallLog);
        var swipe = typeof(AdbReverse).GetMethod("SwipeUpAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var error = await Record.ExceptionAsync(() => (Task)swipe.Invoke(null, [_adbPath, "FAKE1", CancellationToken.None])!);
        Assert.True(error is null, $"the swipe failed: {error?.Message}");
        Assert.Contains("-s FAKE1 shell input swipe 540 2040 540 1200 400", Calls());
    }

    [Fact]
    public async Task TheSwipeUsesTheOverrideSizeWhenOneIsSet()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = FakeAdb(power: "OFF", resumed: "x", focus: "x", overrideSize: "720x1600") }));
        var store = new ConfigStore(path);
        File.Delete(CallLog);
        var swipe = typeof(AdbReverse).GetMethod("SwipeUpAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var error = await Record.ExceptionAsync(() => (Task)swipe.Invoke(null, [_adbPath, "FAKE1", CancellationToken.None])!);
        Assert.True(error is null, $"the swipe failed: {error?.Message}");
        Assert.Contains("-s FAKE1 shell input swipe 360 1360 360 800 400", Calls());
    }

    [Fact]
    public async Task APhoneWhoseScreenStateCannotBeReadIsNotTouched()
    {
        var adb = Adb();
        File.Delete(CallLog);
        FakeAdb(power: "UNKNOWN", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "com.android.launcher/.UxLauncher");
        var error = await Record.ExceptionAsync(() => Wake(adb));
        Assert.True(error is null, $"the wake failed: {error?.Message}");
        var calls = Calls();
        Assert.Contains(calls, c => c == "-s FAKE1 shell dumpsys power"); // the state was asked for
        Assert.DoesNotContain(calls, c => c.Contains("keyevent 224", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("input swipe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheComponentLessKeyguardWindowIsSwipedOpen()
    {
        var adb = Adb();
        File.Delete(CallLog);
        // Android 14's keyguard is a component-less "NotificationShade" window on top of PhoneBox
        FakeAdb(power: "ON", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "NotificationShade");
        var error = await Record.ExceptionAsync(() => Wake(adb));
        Assert.True(error is null, $"the wake failed: {error?.Message}");
        var calls = Calls();
        Assert.DoesNotContain(calls, c => c.Contains("keyevent 224", StringComparison.Ordinal));
        Assert.Contains("-s FAKE1 shell input swipe 540 2040 540 1200 400", calls);
    }

    [Fact]
    public async Task APhoneWithNothingOnScreenIsLeftAlone()
    {
        var adb = Adb();
        File.Delete(CallLog);
        FakeAdb(power: "ON", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "null");
        var error = await Record.ExceptionAsync(() => Wake(adb));
        Assert.True(error is null, $"the wake failed: {error?.Message}");
        var calls = Calls();
        Assert.DoesNotContain(calls, c => c.Contains("keyevent 224", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, c => c.Contains("input swipe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheWakePhoneIsWokenEvenWhenThePanelPhoneIsSomewhereElse()
    {
        // FAKE2 is attached and is the wake phone, but the panel phone selector points at FAKE1: the wake target
        // is looked up in the full attached list, so FAKE2 is woken even though no panel phone is attached.
        var path = Path.Combine(_dir, "config.json");
        var fake = FakeAdb(power: "OFF", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "com.android.launcher/.UxLauncher", device: "FAKE2");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, deviceSerial = "FAKE1", adbWake = new { enabled = true, deviceSerial = "FAKE2" } }));
        var store = new ConfigStore(path);
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        var error = await Record.ExceptionAsync(() => Pass(adb));
        Assert.True(error is null, $"the round failed: {error?.Message}");
        var calls = Calls();
        var key = calls.ToList().FindIndex(c => c == "-s FAKE2 shell input keyevent 224");
        var swipe = calls.ToList().FindIndex(c => c == "-s FAKE2 shell input swipe 540 2040 540 1200 400");
        Assert.True(key >= 0, $"the wake key was not sent to FAKE2: {string.Join(" | ", calls)}");
        Assert.True(swipe > key, $"the swipe was not sent after the wake key: {string.Join(" | ", calls)}");
    }

    [Fact]
    public async Task APhoneIsNeverWokenWhileTheLockedComputerFollowsTheLock()
    {
        var path = Path.Combine(_dir, "config.json");
        var fake = FakeAdb(power: "OFF", resumed: "net.oxog.phonebox.deskpanel/.MainActivity", focus: "com.android.launcher/.UxLauncher");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, adbWake = new { enabled = true, deviceSerial = "FAKE1" }, display = new { followComputerLock = true } }));
        var store = new ConfigStore(path);
        using var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        AdbReverse.ComputerLocked = () => true; // the computer's screen is locked (macOS console session)
        try
        {
            var error = await Record.ExceptionAsync(() => Pass(adb));
            Assert.True(error is null, $"the round failed: {error?.Message}");
            var calls = Calls();
            Assert.DoesNotContain(calls, c => c.Contains("keyevent 224", StringComparison.Ordinal));
            Assert.DoesNotContain(calls, c => c.Contains("input swipe", StringComparison.Ordinal));
        }
        finally { AdbReverse.ComputerLocked = () => false; }
    }

    [Fact]
    public void TheDumpsysParsersReadTheWindowsAndActivities()
    {
        Assert.Equal("net.oxog.phonebox.deskpanel/.MainActivity",
            AdbReverse.CurrentFocusWindow("  mCurrentFocus=Window{a3b2c3d u0 net.oxog.phonebox.deskpanel/.MainActivity}\n"));
        Assert.Equal("NotificationShade",
            AdbReverse.CurrentFocusWindow("  mCurrentFocus=Window{a3b2c3d u0 NotificationShade}\n"));
        Assert.Equal(AdbReverse.NoFocus, AdbReverse.CurrentFocusWindow("  mCurrentFocus=null\n"));
        Assert.Null(AdbReverse.CurrentFocusWindow("  mFocusedApp=AppWindow{a3b2c3d u0 com.android.launcher/.UxLauncher}\n"));
        Assert.Equal("OFF", AdbReverse.PowerState("  Display Power: state=OFF\n"));
        Assert.Equal("ON", AdbReverse.PowerState("  Display Power: state=ON\n"));
        Assert.Null(AdbReverse.PowerState("  Display Power: state=DOZE\n"));
        Assert.Null(AdbReverse.PowerState(""));
        Assert.Equal("net.oxog.phonebox.deskpanel/.MainActivity",
            AdbReverse.LastResumedActivity("  topResumedActivity=ActivityRecord{123 t92 net.oxog.phonebox.deskpanel/.MainActivity}\n"));
        Assert.Null(AdbReverse.LastResumedActivity("ActivityManager: no resumed activity\n"));
    }
}
