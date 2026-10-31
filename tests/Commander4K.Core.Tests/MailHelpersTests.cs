using Commander4K.Core.Mail;
using Commander4K.PluginSdk;
using MailKit;
using MailKit.Security;
using MimeKit;

namespace Commander4K.Core.Tests;

[Collection(nameof(LocalizationTests))]
public sealed class MailHelpersTests
{
    [Fact]
    public void Reserved_device_names_are_made_safe()
    {
        Assert.Equal("Aux_", MailFileNames.LocalFolderName("Aux", '/', MailSpecialFolder.None));
        Assert.Equal("con.txt_", MailFileNames.LocalFolderName("con.txt", '/', MailSpecialFolder.None));
        Assert.Equal(@"Projekty\NUL_", MailFileNames.LocalFolderName("Projekty/NUL", '/', MailSpecialFolder.None));
        Assert.Equal("Console", MailFileNames.LocalFolderName("Console", '/', MailSpecialFolder.None));
    }

    [Fact]
    public void Endpoint_picks_tls_mode_by_port()
    {
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailEndpoints.Parse("imap.x:993", 993).Security);
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailEndpoints.Parse("smtp.x:465", 587).Security);
        Assert.Equal(SecureSocketOptions.StartTls, MailEndpoints.Parse("imap.x:143", 993).Security);
        Assert.Equal(SecureSocketOptions.StartTls, MailEndpoints.Parse("smtp.x:587", 465).Security);
        var e = MailEndpoints.Parse("imap.x", 993);
        Assert.Equal(993, e.Port);
        Assert.Equal("imap.x", e.Host);
        Assert.Throws<FormatException>(() => MailEndpoints.Parse("imaps://x", 993));
        Assert.Throws<FormatException>(() => MailEndpoints.Parse("a b:993", 993));
        Assert.Throws<FormatException>(() => MailEndpoints.Parse(":993", 993));
    }

    [Fact]
    public void FileName_is_valid_unique_and_bounded()
    {
        var date = new DateTimeOffset(2026, 10, 31, 9, 5, 0, TimeSpan.Zero);
        var name = MailFileNames.For(date, "Jan <jan@x.pl>", "Re: <oferta>/\"pilne\"?*|: 🎉", 7);
        Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.Contains("[uid 7]", name);
        Assert.StartsWith("2026-10-31 ", name);

        Assert.Contains("(bez tematu)", MailFileNames.For(date, "a@x.pl", "  ", 1));
        Assert.True(MailFileNames.For(date, new string('s', 300), new string('x', 500), 123456).Length <= 160);

        var a = MailFileNames.For(date, "a@x.pl", "Temat", 1);
        var b = MailFileNames.For(date, "a@x.pl", "Temat", 2);
        Assert.NotEqual(a, b);
        Assert.True(MailFileNames.TryParseUid(a, out var u1) && u1 == 1);
        Assert.True(MailFileNames.TryParseUid(MailFileNames.UnreadPrefix + b, out var u2) && u2 == 2);
        Assert.False(MailFileNames.TryParseUid("notes.eml", out _));
    }

    [Fact]
    public void Special_folders_get_local_names()
    {
        Assert.Equal(MailSpecialFolder.Inbox, MailFileNames.Classify("INBOX", FolderAttributes.None));
        Assert.Equal(MailSpecialFolder.Sent, MailFileNames.Classify("Sent Items", FolderAttributes.None));
        Assert.Equal(MailSpecialFolder.Trash, MailFileNames.Classify("Whatever", FolderAttributes.Trash));
        Assert.Equal(MailSpecialFolder.None, MailFileNames.Classify("Projekty", FolderAttributes.None));
        Assert.Equal("Odebrane", MailFileNames.LocalFolderName("INBOX", '/', MailSpecialFolder.Inbox));
        Assert.Equal("Wysłane", MailFileNames.LocalFolderName("Sent Items", '/', MailSpecialFolder.Sent));
        Assert.Equal(@"Projekty\Klient A", MailFileNames.LocalFolderName("Projekty/Klient A", '/', MailSpecialFolder.None));
    }

    [Fact]
    public void HtmlText_drops_scripts_images_and_unsafe_links()
    {
        var text = HtmlText.ToText("<p>Hej&nbsp;<b>Jan</b></p><script>alert(1)</script><img src=\"https://t.example/p.gif\" alt=\"logo\"><a href=\"javascript:x()\">klik</a><a href=\"https://ok.example/a\">tu</a><style>p{}</style>");
        Assert.Contains("Hej Jan", text);
        Assert.Contains("[obraz: logo]", text);
        Assert.Contains("klik", text);
        Assert.Contains("tu (https://ok.example/a)", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("t.example", text);
        Assert.DoesNotContain("javascript:", text);
        Assert.DoesNotContain("p{}", text);
    }

    [Fact]
    public void Reply_all_excludes_self_and_quotes()
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("A", "a@x"));
        m.To.Add(new MailboxAddress("Me", "me@x"));
        m.To.Add(new MailboxAddress("B", "b@x"));
        m.Cc.Add(new MailboxAddress("C", "c@x"));
        m.Subject = "Re: Plan";
        m.MessageId = "id1@x";
        m.Date = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        m.Body = new TextPart("plain") { Text = "linia 1\nlinia 2" };

        var d = MailDrafts.CreateReply(m, @"C:\m\a.eml", MailReplyMode.ReplyAll, "ME@x");
        Assert.Equal(["a@x"], d.To.Select(t => t.Address));
        Assert.Equal(["b@x", "c@x"], d.Cc.Select(t => t.Address));
        Assert.Equal("Re: Plan", d.Subject);
        Assert.Equal("id1@x", d.InReplyTo);
        Assert.Contains("> linia 1", d.Body);
        Assert.Contains("> linia 2", d.Body);

        var fwd = MailDrafts.CreateReply(m, @"C:\m\a.eml", MailReplyMode.Forward, "me@x");
        Assert.Empty(fwd.To);
        Assert.Equal("Fwd: Re: Plan", fwd.Subject);
        Assert.Null(fwd.InReplyTo);
    }

    [Fact]
    public void Build_round_trips_and_forward_attachments_resolve()
    {
        var orig = new MimeMessage();
        orig.From.Add(new MailboxAddress("A", "a@x"));
        orig.Subject = "S";
        var body = new BodyBuilder { TextBody = "hi" };
        body.Attachments.Add("f.txt", "data"u8.ToArray());
        orig.Body = body.ToMessageBody();
        var eml = new MemoryStream();
        orig.WriteTo(eml, TestContext.Current.CancellationToken);

        var draft = MailDrafts.CreateReply(orig, @"C:\m\a.eml", MailReplyMode.Forward, "me@x");
        Assert.Equal([@"C:\m\a.eml#0"], draft.AttachmentPaths);
        var built = MailDrafts.Build(draft with { To = [new MailAddress(null, "z@x")] }, _ => new MemoryStream(eml.ToArray()));
        Assert.Single(built.Attachments);
        Assert.Equal("f.txt", built.Attachments.First().ContentDisposition?.FileName);
        var view = MailDrafts.View(orig);
        Assert.Equal("hi", view.BodyText.Trim());
        Assert.Single(view.Attachments);
    }

    [Fact]
    public void Since_defaults_to_three_months()
    {
        var now = new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero), MailSince.Resolve(null, now));
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), MailSince.Resolve("10d", now));
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 0, 0, 0, TimeSpan.Zero), MailSince.Resolve("6m", now));
        Assert.Equal(new DateTimeOffset(2025, 10, 31, 0, 0, 0, TimeSpan.Zero), MailSince.Resolve("1y", now));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), MailSince.Resolve("2026-01-01", now));
        Assert.Throws<FormatException>(() => MailSince.Resolve("abc", now));
        Assert.Throws<FormatException>(() => MailSince.Resolve("10000y", now));
    }
}
