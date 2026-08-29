using System.Diagnostics;
using System.Security;
using System.Text;

namespace PNCPKing.Guard;

internal sealed class GuardScheduledTask
{
    private const string TaskName = "PNCP Guard";

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("O Agendador do Windows só está disponível no Windows.");
        }

        if (!enabled)
        {
            await RunSchtasksAsync($"/Delete /TN \"{TaskName}\" /F", ignoreNotFound: true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Não foi possível localizar PNCPGuard.exe.");
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var xml = BuildTaskXml(executable, user);
        var temporary = Path.Combine(Path.GetTempPath(), "pncpguard-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(temporary, xml, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            await RunSchtasksAsync(
                    $"/Create /TN \"{TaskName}\" /XML \"{temporary}\" /F",
                    ignoreNotFound: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string BuildTaskXml(string executable, string user) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Coleta leve de listas de itens do PNCP.</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Repetition><Interval>PT30M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
              <Delay>PT10M</Delay><Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable><RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority>
          </Settings>
          <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(executable)}</Command><Arguments>--background</Arguments></Exec></Actions>
        </Task>
        """;

    private static async Task RunSchtasksAsync(
        string arguments,
        bool ignoreNotFound,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var diagnostic = output + error;
        var taskWasAbsent = diagnostic.Contains("não existe", StringComparison.OrdinalIgnoreCase) ||
                            diagnostic.Contains("não pode encontrar", StringComparison.OrdinalIgnoreCase) ||
                            diagnostic.Contains("não foi possível encontrar", StringComparison.OrdinalIgnoreCase) ||
                            diagnostic.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                            diagnostic.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
        if (process.ExitCode != 0 && !(ignoreNotFound && taskWasAbsent))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        }
    }
}
