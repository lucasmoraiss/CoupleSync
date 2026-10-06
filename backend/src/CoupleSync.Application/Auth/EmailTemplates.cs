using System.Net;
using CoupleSync.Application.Common.Interfaces;

namespace CoupleSync.Application.Auth;

/// <summary>E-mails sent by the auth flows: Portuguese, plain text plus minimal HTML, no external images.</summary>
public static class EmailTemplates
{
    public static EmailMessage EmailVerification(string toAddress, string name, string code, int validMinutes) =>
        Build(
            toAddress, name,
            subject: "Confirme seu e-mail no CoupleSync",
            intro: "Use o código abaixo no app para confirmar seu e-mail:",
            code, validMinutes,
            outro: "Se você não criou uma conta no CoupleSync, ignore esta mensagem.");

    public static EmailMessage PasswordReset(string toAddress, string name, string code, int validMinutes) =>
        Build(
            toAddress, name,
            subject: "Seu código para redefinir a senha do CoupleSync",
            intro: "Use o código abaixo no app para criar uma nova senha:",
            code, validMinutes,
            outro: "Se você não pediu para redefinir a senha, ignore esta mensagem: sua senha continua a mesma.");

    private static EmailMessage Build(string toAddress, string name, string subject, string intro, string code, int validMinutes, string outro)
    {
        var greeting = $"Olá, {name}!";
        var validity = $"O código vale por {validMinutes} minutos e só pode ser usado uma vez.";

        var text = string.Join("\n\n", greeting, intro, code, validity, outro, "Equipe CoupleSync");

        var html =
            "<!DOCTYPE html><html lang=\"pt-BR\"><body style=\"font-family:Arial,Helvetica,sans-serif;color:#222;\">"
            + $"<p>{WebUtility.HtmlEncode(greeting)}</p>"
            + $"<p>{WebUtility.HtmlEncode(intro)}</p>"
            + $"<p style=\"font-size:28px;font-weight:bold;letter-spacing:6px;\">{WebUtility.HtmlEncode(code)}</p>"
            + $"<p>{WebUtility.HtmlEncode(validity)}</p>"
            + $"<p>{WebUtility.HtmlEncode(outro)}</p>"
            + "<p>Equipe CoupleSync</p></body></html>";

        return new EmailMessage(toAddress, name, subject, html, text);
    }
}
