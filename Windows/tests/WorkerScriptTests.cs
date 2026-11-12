using System.Diagnostics;

namespace GigaTTS.Tests;

public class WorkerScriptTests
{
    [Fact]
    public void Truststore_is_injected_before_model_imports_and_verification_stays_on()
    {
        var text = File.ReadAllText(RepoFiles.Find("Worker/worker.py"));
        var inject = text.IndexOf("truststore.inject_into_ssl()", StringComparison.Ordinal);
        var torch = text.IndexOf("import torch", StringComparison.Ordinal);
        var gigaam = text.IndexOf("import gigaam", StringComparison.Ordinal);

        Assert.True(inject >= 0);
        Assert.True(torch > inject);
        Assert.True(gigaam > inject);
        Assert.DoesNotContain("PYTHONHTTPSVERIFY", text);
        Assert.DoesNotContain("_create_unverified_context", text);
        Assert.DoesNotContain("CERT_NONE", text);
        Assert.DoesNotContain("verify=False", text);
        Assert.DoesNotContain("check_hostname", text);
    }

    [Fact]
    public void Requirements_list_truststore_and_pyannote()
    {
        var text = File.ReadAllText(RepoFiles.Find("Worker/requirements.txt"));
        Assert.Contains("truststore", text);
        Assert.Contains("pyannote.audio", text);
        Assert.Contains("huggingface_hub", text);
    }

    [Fact]
    public void Retry_load_installs_truststore_without_torch()
    {
        var args = WorkerRuntime.TruststoreInstallArguments;
        Assert.Equal(["-m", "pip", "install", "--disable-pip-version-check", "truststore"], args);
        Assert.DoesNotContain(args, arg => arg.Contains("torch", StringComparison.OrdinalIgnoreCase));

        var source = File.ReadAllText(RepoFiles.Find("Windows/src/Worker.cs"));
        Assert.Contains("TruststoreInstallArguments", source);
        Assert.DoesNotContain("PYTHONHTTPSVERIFY", source);
    }

    [Fact]
    public void Worker_script_parses()
    {
        var python = Python3();
        if (python == null)
            return;

        var script = RepoFiles.Find("Worker/worker.py");
        var info = new ProcessStartInfo(python)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        info.ArgumentList.Add("-m");
        info.ArgumentList.Add("py_compile");
        info.ArgumentList.Add(script);
        using var process = Process.Start(info);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(20_000));
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, error);
    }

    static string? Python3()
    {
        foreach (var candidate in new[] { "/usr/bin/python3", "/opt/homebrew/bin/python3" })
        {
            if (File.Exists(candidate))
                return candidate;
        }
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "python3");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
