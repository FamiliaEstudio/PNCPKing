using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PNCPKing.App.Services;

internal static class DiagnosticEmailDialog
{
    internal static Task ShowErrorAsync(Exception error, string message, string title, Window? parent = null)
    {
        if (Application.Current is App { DiagnosticLog: { } log })
            return OfferAsync(log, message, log.Error("interface", title, error), title, parent);

        if (parent is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(parent, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }

    internal static Task ReportAsync(AppDiagnosticLog log, string area, string context, Exception error, string title)
    {
        var incident = log.Error(area, context, error);
        return OfferAsync(log, $"{context}\n\n{error.Message}", incident, title);
    }

    internal static async Task OfferAsync(AppDiagnosticLog log, string message, string incident, string title = "PNCP King", Window? parent = null)
    {
        var prompt =
            $"{message}\n\nLog da sessão: {log.FilePath}\n\n" +
            $"Autoriza enviar o registro deste erro para {DiagnosticMail.Recipient}? " +
            "O anexo de texto inclui os detalhes técnicos do erro e pode conter caminhos locais e definições SQL.";
        var consent = parent is { IsVisible: true }
            ? MessageBox.Show(parent, prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No)
            : MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
        if (consent != MessageBoxResult.Yes) return;

        var owner = (parent ?? Application.Current?.MainWindow) is { } window ? new WindowInteropHelper(window).Handle : 0;
        try
        {
            var result = await Task.Run(() => DiagnosticMail.Submit(
                log.DirectoryPath, incident, typeof(AppDiagnosticLog).Assembly.GetName().Version!, owner));
            if (result.Status == DiagnosticMail.Outcome.Canceled) return;
            if (result.Status == DiagnosticMail.Outcome.Submitted)
            {
                MessageBox.Show("O programa de e-mail aceitou o diagnóstico para envio.", title);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(result.DraftPath!) { UseShellExecute = true });
                MessageBox.Show("O envio automático ficou indisponível. O rascunho já tem destinatário e anexo. " +
                    $"Clique em Enviar no seu programa de e-mail.\n\nPara usar webmail, anexe este arquivo a uma mensagem " +
                    $"para {DiagnosticMail.Recipient}:\n{result.ReportPath}", title);
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
            {
                MessageBox.Show($"O diagnóstico está pronto para envio manual. Anexe o arquivo abaixo a uma mensagem " +
                    $"para {DiagnosticMail.Recipient}.\n\n{result.ReportPath}", title);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException or NotSupportedException)
        {
            MessageBox.Show($"Não foi possível preparar o envio. {error.Message}\n\n" +
                $"O log da sessão está em {log.FilePath}.", title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
