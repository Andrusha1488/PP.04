
using MimeKit;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace StudentDiaryWeb.Services
{
    public class EmailService
    {
        private readonly string _fromEmail;
        private readonly string _fromName;
        private readonly string _password;
        private readonly string _smtpHost;
        private readonly int _smtpPort;

        public EmailService(IConfiguration config)
        {
            _fromEmail = config["Email:From"] ?? "";
            _fromName = config["Email:FromName"] ?? "Электронный дневник";
            _password = config["Email:Password"] ?? "";
            _smtpHost = config["Email:SmtpHost"] ?? "smtp.gmail.com";
            _smtpPort = int.Parse(config["Email:SmtpPort"] ?? "587");
        }

        public async Task SendAsync(string toEmail, string subject, string body)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_fromName, _fromEmail));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = subject;
            message.Body = new TextPart("html") { Text = body };

            using var client = new SmtpClient();
            await client.ConnectAsync(_smtpHost, _smtpPort,
                SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(_fromEmail, _password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
    }
}
