using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

/// <summary>
/// The wake-and-unlock a newly plugged-in phone gets when Settings › Display › wake and unlock is on: after the phone
/// is woken, the agent asks it whether a keyguard is up and only then steps over it, so nothing that is not a lock
/// screen ever receives the swipe. A secure PIN, pattern or password is never passed: "wm dismiss-keyguard" refuses
/// those by itself, which is Android's rule and not one this agent enforces. On Windows this is the only unlock the
/// agent does, since following the computer's lock (next door, in the apply-screen test) is macOS-only.
///
/// Like the other ADB tests, this runs the real AdbReverse against a fake adb that logs its calls and answers with
/// canned text, never the real adb or a real phone. The parser is fed the lines real Android builds print, in both
/// orders, on a build that prints neither flag, and on a dump that cannot be read.
///
/// The file is kept ASCII, as the other ADB tests are: characters a Windows console mangles on an old codepage make a
/// diff hard to read, so the languages the parser is tried against are named here and written as escapes in the
/// fixtures, which JSON and UTF-8 pass through untouched.
/// </summary>
[Collection("adb session")] // SessionEndingTests flips the static session-ending window
public sealed class AdbWakeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phonebox-adb-wake-" + Guid.NewGuid().ToString("N"));
    public AdbWakeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string CallLog => Path.Combine(_dir, "calls.log");

    /// <summary>
    /// A fake adb with <paramref name="devices"/> attached and the PhoneBox panel installed. Its "dumpsys window"
    /// answer is <paramref name="windowDump"/>, which is what decides whether the keyguard gets stepped over.
    /// </summary>
    private string FakeAdb(string windowDump, string[]? devices = null)
    {
        devices ??= ["FAKE1"];
        // cmd.exe ends a parenthesised group at the first ")", so a dump that carries one is escaped.
        var batch = windowDump.Replace(")", "^)");
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "adb.cmd");
            File.WriteAllText(cmd, string.Join("\r\n",
                "@echo off",
                ">>\"%~dp0calls.log\" echo %*",
                "if \"%1\"==\"devices\" (echo List of devices attached" + string.Concat(devices.Select(d => "& echo " + d + "\tdevice")) + "& exit /b 0)",
                "if \"%4\"==\"getprop\" (exit /b 0)",
                "if \"%3\"==\"reverse\" if \"%4\"==\"--list\" (exit /b 0)",
                "if \"%4\"==\"pm\" (echo package:/data/app/base.apk& exit /b 0)",
                "if \"%4\"==\"dumpsys\" if \"%5\"==\"window\" (echo " + batch + "& exit /b 0)",
                "exit /b 0", ""));
            return cmd;
        }
        var sh = Path.Combine(_dir, "adb");
        File.WriteAllText(sh, string.Join("\n",
            "#!/bin/sh",
            "echo \"$*\" >> \"$(dirname \"$0\")/calls.log\"",
            "if [ \"$1\" = devices ]; then printf 'List of devices attached\\n" + string.Concat(devices.Select(d => d + "\\tdevice\\n")) + "'; exit 0; fi",
            "if [ \"$4\" = getprop ]; then exit 0; fi",
            "if [ \"$3\" = reverse ] && [ \"$4\" = --list ]; then exit 0; fi",
            "if [ \"$4\" = pm ]; then echo package:/data/app/base.apk; exit 0; fi",
            "if [ \"$4\" = dumpsys ] && [ \"$5\" = window ]; then echo '" + windowDump + "'; exit 0; fi",
            "exit 0", ""));
        File.SetUnixFileMode(sh, (UnixFileMode)0b111_101_101);
        return sh;
    }

    /// <summary>
    /// What the fake adb was called with, read the same retrying way as in the reverse test: cmd.exe holds the log
    /// without sharing it while a call is being logged, and a test that read it then used to fail on the runners.
    /// </summary>
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

    /// <summary>Runs the loop until <paramref name="done"/> holds or three rounds ("adb devices" calls) went by.</summary>
    private async Task<string[]> RunUntil(string fake, object settings, Func<string[], bool> done)
    {
        var cfgPath = Path.Combine(_dir, "config.json");
        File.WriteAllText(cfgPath, JsonSerializer.Serialize(settings));
        var store = new ConfigStore(cfgPath);
        Assert.Equal(fake, AdbReverse.FindAdb(store.Get().AdbPath)); // never reach the real adb
        var adb = new AdbReverse(store, NullLogger<AdbReverse>.Instance);
        using var cts = new CancellationTokenSource();
        await adb.StartAsync(cts.Token);
        try
        {
            for (var i = 0; i < 300; i++)
            {
                var calls = Calls();
                if (done(calls) || calls.Count(c => c == "devices") >= 3) break;
                await Task.Delay(100);
            }
            return Calls();
        }
        finally { cts.Cancel(); try { await adb.StopAsync(CancellationToken.None); } catch { } }
    }

    /// <summary>Wakes the phone and brings the panel up the way the follow-the-computer-lock path does, with the
    /// unlock stepped over only when the phone says a keyguard is up.</summary>
    private static async Task ApplyScreenAsync(AdbReverse adb, string device, bool locked)
    {
        var apply = typeof(AdbReverse).GetMethod("ApplyScreenAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)apply.Invoke(adb, [device, locked, CancellationToken.None])!;
    }

    [Theory] // the two flags real Android builds print, in either order, and what stands in for them when they are absent
    [InlineData("mShowingLockscreen=true", true)]
    [InlineData("isKeyguardShowing=true", true)]
    [InlineData("mShowingLockscreen=false\n  isKeyguardShowing=true", true)]
    [InlineData("isKeyguardShowing=true\nmShowingLockscreen=false", true)]
    [InlineData("", false)]
    [InlineData("\n", false)]
    [InlineData("mShowingLockscreen=false\nisKeyguardShowing=false", false)]
    // an older build (the 5.1 emulator in CI) names the keyguard service without saying whether it is showing
    [InlineData("mKeyguardServiceConnected=true\n  KeyguardServiceDelegate: mDeviceInteractive=true", false)]
    // a dump that could not be read at all, which must not be taken for a keyguard
    [InlineData("error: device offline", false)]
    [InlineData("error: closed", false)]
    public void KeyguardShowingFollowsTheFlagOfEachAndroidBuild(string dump, bool expected)
    {
        Assert.Equal(expected, AdbReverse.KeyguardShowing(dump));
    }

    [Fact]
    public void WakeOnUnlockIsOffByDefault()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, "{}");
        Assert.False(new ConfigStore(path).Get().Display.WakeOnUnlock);
    }

    /// <summary>
    /// A phone that sleeps on its keyguard needs the keyguard stepped over, not just woken: waking alone leaves the
    /// panel behind the lock screen. The agent asks first, so a phone with no keyguard never gets a swipe that would
    /// land on whatever it happens to be showing instead.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APluggedInPhoneIsUnlockedOnlyWhenTheSettingIsOn(bool wakeOnUnlock)
    {
        var fake = FakeAdb("mShowingLockscreen=true");
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1",
                display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = true, wakeOnUnlock } },
            c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));

        var opened = Array.IndexOf(calls, "-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity");
        Assert.True(opened >= 0, "the panel is opened:\n" + string.Join("\n", calls));
        Assert.Contains("-s FAKE1 shell input keyevent KEYCODE_WAKEUP", calls);
        var asked = Array.IndexOf(calls, "-s FAKE1 shell dumpsys window");
        var dismissed = Array.IndexOf(calls, "-s FAKE1 shell wm dismiss-keyguard");
        if (!wakeOnUnlock)
        {
            Assert.False(dismissed >= 0, "nothing is stepped over without the setting:\n" + string.Join("\n", calls));
            // the phone is not even asked, since an agent that will not unlock has no use for the answer
            Assert.False(asked >= 0 && asked < opened, "the setting is read before the phone is asked:\n" + string.Join("\n", calls));
            return;
        }
        Assert.True(asked >= 0, "the phone is asked whether a keyguard is up:\n" + string.Join("\n", calls));
        Assert.True(dismissed >= 0, "the keyguard is stepped over:\n" + string.Join("\n", calls));
        // asked, stepped over, panel: the answer has to arrive before the swipe, the swipe before the panel is shown
        Assert.True(asked < dismissed);
        Assert.True(dismissed < opened);
    }

    /// <summary>A phone that reports no keyguard (already unlocked, or a swipe-less lock) is not asked to dismiss one,
    /// and the panel still comes up.</summary>
    [Fact]
    public async Task APluggedInPhoneWithNoKeyguardKeepsWhatItIsShowing()
    {
        var fake = FakeAdb("mShowingLockscreen=false\nisKeyguardShowing=false");
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1",
                display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = true, wakeOnUnlock = true } },
            c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));

        Assert.Contains("-s FAKE1 shell dumpsys window", calls);
        Assert.DoesNotContain("-s FAKE1 shell wm dismiss-keyguard", calls);
        Assert.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", calls);
    }

    /// <summary>A build this release cannot read (neither flag, as on old Android) still gets the panel: not being able
    /// to answer about the keyguard costs the unlock, never the panel itself.</summary>
    [Fact]
    public async Task AnOldPhoneStillGetsThePanelWhenItCannotAnswerAboutItsKeyguard()
    {
        var fake = FakeAdb("mKeyguardServiceConnected=true\n  KeyguardServiceDelegate: mDeviceInteractive=true");
        var calls = await RunUntil(fake, new { adbPath = fake, deviceSerial = "FAKE1",
                display = new { homeScreen = false, keepAwake = false, followComputerLock = false, openOnConnect = true, wakeOnUnlock = true } },
            c => c.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity"));

        Assert.DoesNotContain("-s FAKE1 shell wm dismiss-keyguard", calls);
        Assert.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", calls);
    }

    /// <summary>The follow-the-computer-lock path already asked Android to step over the keyguard every time the
    /// computer unlocked, whether or not the phone was on one. It now asks first, so a phone that is sitting on the
    /// panel gets the wake and nothing else.</summary>
    [Theory]
    [InlineData(true, true)]  // on its keyguard: stepped over
    [InlineData(false, false)] // already unlocked: no swipe, nothing else disturbed
    public async Task ThePhoneIsOnlySteppedOverWhenItIsOnItsKeyguard(bool keyguard, bool expectDismissed)
    {
        var fake = FakeAdb(keyguard ? "isKeyguardShowing=true" : "isKeyguardShowing=false");
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { adbPath = fake, deviceSerial = "FAKE1",
            display = new { homeScreen = false, keepAwake = false, followComputerLock = true } }));
        using var adb = new AdbReverse(new ConfigStore(path), NullLogger<AdbReverse>.Instance);

        await ApplyScreenAsync(adb, "FAKE1", locked: false);

        Assert.Contains("-s FAKE1 shell input keyevent KEYCODE_WAKEUP", Calls());
        Assert.Contains("-s FAKE1 shell am start -n net.oxog.phonebox.deskpanel/.MainActivity", Calls());
        Assert.Equal(expectDismissed, Calls().Contains("-s FAKE1 shell wm dismiss-keyguard"));
    }
}
