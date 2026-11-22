using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The wake-and-unlock feature (against a fake adb that logs its calls; never the real adb or a phone):
/// a phone whose last resumed activity is PhoneBox is woken (KEYCODE_WAKEUP) and swiped up; one showing
/// anything else is left alone; a screen that is already on and locked only gets the swipe.
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
    /// FAKE1 attached; its screen is <paramref name="power"/> ("OFF" / "ON"), its last resumed activity is
    /// <paramref name="resumed"/>, and its current focus is <paramref name="focus"/>.
    /// </summary>
    private string FakeAdb(string power, string resumed, string focus)
    {
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%1\"==\"devices\" (echo List of devices attached& echo FAKE1\tdevice& exit /b 0)",
                $"if \"%3\"==\"shell\" if \"%4\"==\"dumpsys\" if \"%5\"==\"power\" (echo Display Power: state={power}& exit /b 0)",
                $"if \"%3\"==\"shell\" if \"%4\"==\"dumpsys\" if \"%5\"==\"activity\" if \"%6\"==\"activities\" (echo   topResumedActivity=ActivityRecord{{123 t92 {resumed}}}& exit /b 0)",
                $"if \"%3\"==\"shell\" if \"%4\"==\"dumpsys\" if \"%5\"==\"window\" (echo   mCurrentFocus=Window{{abc u0 {focus}}}& exit /b 0)",
                "if \"%3\"==\"shell\" if \"%4\"==\"wm\" if \"%5\"==\"size\" (echo Physical size: 1080x2400& exit /b 0)",
                "exit /b 0", ""));
            _adbPath = cmd;
            return cmd;
        }
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            "if [ \"$1\" = devices ]; then printf 'List of devices attached\\nFAKE1\\tdevice\\n'; exit 0; fi",
            $"if [ \"$3\" = shell ] && [ \"$4\" = dumpsys ] && [ \"$5\" = power ]; then echo 'Display Power: state={power}'; exit 0; fi",
            $"if [ \"$3\" = shell ] && [ \"$4\" = dumpsys ] && [ \"$5\" = activity ] && [ \"$6\" = activities ]; then echo '  topResumedActivity=ActivityRecord{{123 t92 {resumed}}}'; exit 0; fi",
            $"if [ \"$3\" = shell ] && [ \"$4\" = dumpsys ] && [ \"$5\" = window ]; then echo '  mCurrentFocus=Window{{abc u0 {focus}}}'; exit 0; fi",
            "if [ \"$3\" = shell ] && [ \"$4\" = wm ] && [ \"$5\" = size ]; then echo 'Physical size: 1080x2400'; exit 0; fi",
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
    public void TheDumpsysParsersReadTheActivities()
    {
        Assert.Equal("net.oxog.phonebox.deskpanel/.MainActivity",
            AdbReverse.CurrentFocusActivity("  mCurrentFocus=Window{a3b2c3d u0 net.oxog.phonebox.deskpanel/.MainActivity}\n"));
        Assert.Null(AdbReverse.CurrentFocusActivity("  mCurrentFocus=null\n"));
        Assert.Equal("net.oxog.phonebox.deskpanel/.MainActivity",
            AdbReverse.LastResumedActivity("  topResumedActivity=ActivityRecord{123 t92 net.oxog.phonebox.deskpanel/.MainActivity}\n"));
        Assert.Null(AdbReverse.LastResumedActivity("ActivityManager: no resumed activity\n"));
    }
}
