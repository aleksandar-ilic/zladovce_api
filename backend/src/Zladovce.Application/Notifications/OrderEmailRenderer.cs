using System.Globalization;
using System.Net;
using System.Text;
using Zladovce.Domain.Common;
using Zladovce.Domain.Notifications;
using Zladovce.Domain.Orders;
using Zladovce.Domain.Tracking;

namespace Zladovce.Application.Notifications;

/// <summary>Store data shown in every email, read from the store settings when the email is rendered (never invented).</summary>
/// <param name="LogoUrl">Absolute https URL of the logo (<c>Email:LogoUrl</c>); without it the store name is the header.</param>
/// <param name="StorefrontUrl">The shop's base URL, linked in the footer.</param>
public sealed record EmailBranding(string StoreName, string? ContactEmail, string? Phone, string? BusinessAddress, string? LogoUrl, string? StorefrontUrl);

/// <summary>
/// The six order emails (P1.7) as HTML and plain text, Serbian Latin. Plain C# string templates: every value that comes from
/// data is HTML-encoded (<see cref="WebUtility.HtmlEncode"/>) exactly once where it is written, and links are only emitted for
/// absolute http(s) URLs. Layout: tables and inline CSS (Gmail/Outlook), at most 600 px, cream background, olive accents,
/// serif headings, no external images except the configured logo, no tracking pixels.
/// </summary>
public static class OrderEmailRenderer
{
    // Brand colours (cream, olive, sage, earth).
    private const string Cream = "#F6F1E7";
    private const string Card = "#FFFDF8";
    private const string Olive = "#5B6B2F";
    private const string OliveDark = "#45521F";
    private const string Sage = "#E8ECDC";
    private const string Ink = "#2F2A22";
    private const string Muted = "#6B6355";
    private const string Line = "#E4DCCB";
    private const string Serif = "Georgia, 'Times New Roman', Times, serif";
    private const string Sans = "'Helvetica Neue', Helvetica, Arial, sans-serif";

    private static readonly NumberFormatInfo SerbianNumbers = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberGroupSizes = [3],
        NegativeSign = "-",
    };

    /// <summary><c>1.234,00 RSD</c>.</summary>
    public static string FormatMoney(decimal amount) => amount.ToString("N2", SerbianNumbers) + " " + Money.Currency;

    /// <summary><c>14.11.2026. u 15:30</c> in Europe/Belgrade.</summary>
    public static string FormatDateTime(DateTimeOffset instant)
    {
        var local = SerbianTime.ToLocal(instant);
        return local.ToString("dd.MM.yyyy.", CultureInfo.InvariantCulture) + " u " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary><c>14.11.2026.</c> in Europe/Belgrade.</summary>
    public static string FormatDate(DateTimeOffset instant) => SerbianTime.ToLocal(instant).ToString("dd.MM.yyyy.", CultureInfo.InvariantCulture);

    public static string PaymentMethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.CashOnDelivery => "Plaćanje pouzećem",
        _ => method.ToString(),
    };

    public static string Subject(EmailTemplate template, OrderEmailPayload payload) => template switch
    {
        EmailTemplate.OrderReceived when payload.IsResent => $"Potvrda porudžbine {payload.OrderNumber}",
        EmailTemplate.OrderReceived => $"Porudžbina {payload.OrderNumber} je primljena",
        EmailTemplate.OrderAccepted => $"Porudžbina {payload.OrderNumber} je prihvaćena",
        EmailTemplate.OrderPreparing => $"Porudžbina {payload.OrderNumber} se priprema",
        EmailTemplate.OrderShipped => $"Porudžbina {payload.OrderNumber} je poslata",
        EmailTemplate.OrderDelivered => $"Porudžbina {payload.OrderNumber} je isporučena",
        EmailTemplate.OrderCancelled when payload.Status == OrderStatus.Rejected => $"Porudžbina {payload.OrderNumber} nije prihvaćena",
        EmailTemplate.OrderCancelled => $"Porudžbina {payload.OrderNumber} je otkazana",
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, null),
    };

    /// <param name="trackingCode">The plain code (any accepted form); null renders the email without code and link.</param>
    public static RenderedEmail Render(EmailTemplate template, OrderEmailPayload payload, string? trackingCode, EmailBranding branding)
    {
        var subject = Subject(template, payload);
        var grouped = trackingCode is null ? null : TrackingCode.Format(trackingCode);
        var trackingLink = grouped is not null && IsHttpUrl(payload.TrackingPageUrl)
            ? StorefrontOptions.TrackingLink(payload.TrackingPageUrl, grouped)
            : null;

        var w = new Writer();
        var (heading, intro, preheader) = Texts(template, payload, branding);

        w.Heading(heading);
        w.Paragraph($"Zdravo, {payload.FirstName}!");
        foreach (var paragraph in intro)
        {
            w.Paragraph(paragraph);
        }

        w.KeyValues(
        [
            ("Broj porudžbine", payload.OrderNumber),
            ("Datum porudžbine", FormatDateTime(payload.PlacedAt)),
        ]);

        if (payload.CustomerMessage is { } message)
        {
            w.MessageBox(payload.Status == OrderStatus.Rejected ? "Obrazloženje" : $"Poruka od {branding.StoreName}", message);
        }

        if (template == EmailTemplate.OrderReceived)
        {
            WriteSummary(w, payload);
        }

        if (template == EmailTemplate.OrderShipped && payload.Shipment is { } shipment)
        {
            WriteShipment(w, shipment);
        }

        if (grouped is not null)
        {
            w.TrackingBox(grouped, trackingLink, template == EmailTemplate.OrderReceived);
        }

        WriteContact(w, branding);

        var footer =
            $"Ovu poruku ste dobili jer je sa vašom adresom e-pošte naručeno u prodavnici {branding.StoreName}. " +
            $"Šaljemo je samo radi obaveštenja o porudžbini {payload.OrderNumber}.";

        return new RenderedEmail(subject, w.Html(subject, preheader, branding, footer), w.Text(subject, branding, footer));
    }

    private static (string Heading, IReadOnlyList<string> Intro, string Preheader) Texts(EmailTemplate template, OrderEmailPayload p, EmailBranding b) =>
        template switch
        {
            EmailTemplate.OrderReceived when p.IsResent => (
                "Potvrda vaše porudžbine",
                ["Ponovo vam šaljemo potvrdu porudžbine. Kod za praćenje u ovoj poruci je nov; koristite njega za praćenje porudžbine."],
                $"Potvrda porudžbine {p.OrderNumber} sa kodom za praćenje."),
            EmailTemplate.OrderReceived => (
                "Hvala na porudžbini!",
                [$"Primili smo vašu porudžbinu {p.OrderNumber}. Uskoro ćemo je pregledati i javiti vam se kada je prihvatimo."],
                $"Primili smo porudžbinu {p.OrderNumber}. U poruci su pregled porudžbine i kod za praćenje."),
            EmailTemplate.OrderAccepted => (
                "Porudžbina je prihvaćena",
                ["Vašu porudžbinu smo prihvatili. Uskoro počinjemo sa pripremom proizvoda."],
                $"Porudžbina {p.OrderNumber} je prihvaćena."),
            EmailTemplate.OrderPreparing => (
                "Pripremamo vašu porudžbinu",
                ["Vaši proizvodi se upravo pripremaju i pakuju. Javićemo vam kada pošiljku predamo kurirskoj službi."],
                $"Porudžbina {p.OrderNumber} se priprema."),
            EmailTemplate.OrderShipped => (
                "Porudžbina je na putu",
                ["Pošiljku smo predali kurirskoj službi. Porudžbinu plaćate pouzećem, kuriru prilikom preuzimanja."],
                $"Porudžbina {p.OrderNumber} je predata kurirskoj službi."),
            EmailTemplate.OrderDelivered => (
                "Porudžbina je isporučena",
                [$"Vaša porudžbina je isporučena. Hvala vam što ste izabrali {b.StoreName}. Prijatno!"],
                $"Porudžbina {p.OrderNumber} je isporučena."),
            EmailTemplate.OrderCancelled when p.Status == OrderStatus.Rejected => (
                "Porudžbina nije prihvaćena",
                ["Nažalost, nismo u mogućnosti da prihvatimo vašu porudžbinu. Pošto se plaća pouzećem, ništa niste platili niti dugujete."],
                $"Porudžbina {p.OrderNumber} nije prihvaćena."),
            EmailTemplate.OrderCancelled => (
                "Porudžbina je otkazana",
                ["Vaša porudžbina je otkazana. Pošto se plaća pouzećem, ništa niste platili niti dugujete."],
                $"Porudžbina {p.OrderNumber} je otkazana."),
            _ => throw new ArgumentOutOfRangeException(nameof(template), template, null),
        };

    private static void WriteSummary(Writer w, OrderEmailPayload p)
    {
        w.SectionTitle("Pregled porudžbine");
        if (p.Lines is { Count: > 0 } lines)
        {
            w.Items(lines);
        }

        var totals = new List<(string, string)>();
        if (p.Subtotal is { } subtotal)
        {
            totals.Add(("Proizvodi", FormatMoney(subtotal)));
        }

        if (p.ShippingPrice is { } shipping)
        {
            totals.Add(("Dostava", shipping == 0 ? "Besplatna" : FormatMoney(shipping)));
        }

        w.Totals(totals, p.Total is { } total ? ("Ukupno za plaćanje", FormatMoney(total)) : null);

        var details = new List<(string, string)> { ("Način plaćanja", PaymentMethodLabel(p.PaymentMethod) + " (gotovinom kuriru pri preuzimanju)") };
        if (p.ShippingAddress is { } address)
        {
            details.Add(("Adresa za dostavu", $"{address.Street}, {address.PostalCode} {address.City}"));
        }

        w.KeyValues(details);
    }

    private static void WriteShipment(Writer w, OrderEmailShipment shipment)
    {
        w.SectionTitle("Podaci o pošiljci");
        var rows = new List<(string, string)> { ("Kurirska služba", shipment.CourierName) };
        if (shipment.TrackingNumber is { } number)
        {
            rows.Add(("Broj pošiljke", number));
        }

        rows.Add(("Datum slanja", FormatDate(shipment.ShippedAt)));
        w.KeyValues(rows);

        if (shipment.TrackingUrl is { } url && IsHttpUrl(url))
        {
            w.Link("Pratite pošiljku na stranici kurirske službe", url);
        }
    }

    private static void WriteContact(Writer w, EmailBranding b)
    {
        var rows = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(b.ContactEmail))
        {
            rows.Add(("E-pošta", b.ContactEmail));
        }

        if (!string.IsNullOrWhiteSpace(b.Phone))
        {
            rows.Add(("Telefon", b.Phone));
        }

        if (!string.IsNullOrWhiteSpace(b.BusinessAddress))
        {
            rows.Add(("Adresa", b.BusinessAddress));
        }

        if (rows.Count == 0)
        {
            return;
        }

        w.SectionTitle("Imate pitanje?");
        w.Paragraph("Rado ćemo vam pomoći. Navedite broj porudžbine kada nam se javite.");
        w.KeyValues(rows);
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string E(string value) => WebUtility.HtmlEncode(value);

    /// <summary>Writes HTML and plain text side by side.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _html = new();
        private readonly StringBuilder _text = new();

        public void Heading(string text)
        {
            _html.Append($"<h1 style=\"margin:0 0 16px 0;font-family:{Serif};font-size:26px;line-height:1.3;font-weight:normal;color:{Ink};\">{E(text)}</h1>");
            _text.AppendLine(text).AppendLine(new string('=', Math.Min(text.Length, 60))).AppendLine();
        }

        public void Paragraph(string text)
        {
            _html.Append($"<p style=\"margin:0 0 14px 0;font-family:{Sans};font-size:15px;line-height:1.6;color:{Ink};\">{E(text)}</p>");
            _text.AppendLine(text).AppendLine();
        }

        public void SectionTitle(string text)
        {
            _html.Append($"<h2 style=\"margin:24px 0 10px 0;font-family:{Serif};font-size:19px;line-height:1.3;font-weight:normal;color:{Olive};\">{E(text)}</h2>");
            _text.AppendLine(text.ToUpperInvariant()).AppendLine();
        }

        public void KeyValues(IReadOnlyList<(string Key, string Value)> rows)
        {
            _html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 14px 0;\">");
            foreach (var (key, value) in rows)
            {
                _html.Append(
                    $"<tr><td style=\"padding:4px 12px 4px 0;font-family:{Sans};font-size:14px;line-height:1.5;color:{Muted};width:40%;vertical-align:top;\">{E(key)}</td>" +
                    $"<td style=\"padding:4px 0;font-family:{Sans};font-size:14px;line-height:1.5;color:{Ink};vertical-align:top;\"><strong>{E(value)}</strong></td></tr>");
                _text.AppendLine($"{key}: {value}");
            }

            _html.Append("</table>");
            _text.AppendLine();
        }

        public void MessageBox(string title, string message)
        {
            _html.Append(
                $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:8px 0 18px 0;\"><tr>" +
                $"<td style=\"background:{Sage};border-left:4px solid {Olive};padding:14px 16px;font-family:{Sans};font-size:14px;line-height:1.6;color:{Ink};\">" +
                $"<strong style=\"color:{OliveDark};\">{E(title)}</strong><br>{E(message).ReplaceLineEndings("<br>")}</td></tr></table>");
            _text.AppendLine($"{title}:").AppendLine(message).AppendLine();
        }

        public void Items(IReadOnlyList<OrderEmailLine> lines)
        {
            const string head = "padding:8px 6px;font-family:" + Sans + ";font-size:12px;line-height:1.4;color:" + Muted + ";text-transform:uppercase;letter-spacing:0.5px;border-bottom:1px solid " + Line + ";";
            const string cell = "padding:10px 6px;font-family:" + Sans + ";font-size:14px;line-height:1.5;color:" + Ink + ";border-bottom:1px solid " + Line + ";vertical-align:top;";
            _html.Append(
                "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 8px 0;border-collapse:collapse;\">" +
                $"<tr><td style=\"{head}\">Proizvod</td><td align=\"center\" style=\"{head}\">Kol.</td><td align=\"right\" style=\"{head}\">Cena</td><td align=\"right\" style=\"{head}\">Ukupno</td></tr>");
            foreach (var line in lines)
            {
                _html.Append(
                    $"<tr><td style=\"{cell}\">{E(line.ProductName)}<br><span style=\"font-size:13px;color:{Muted};\">{E(line.VariantName)}</span></td>" +
                    $"<td align=\"center\" style=\"{cell}\">{line.Quantity}</td>" +
                    $"<td align=\"right\" style=\"{cell}white-space:nowrap;\">{E(FormatMoney(line.UnitPrice))}</td>" +
                    $"<td align=\"right\" style=\"{cell}white-space:nowrap;\">{E(FormatMoney(line.LineTotal))}</td></tr>");
                _text.AppendLine($"- {line.ProductName} ({line.VariantName}): {line.Quantity} × {FormatMoney(line.UnitPrice)} = {FormatMoney(line.LineTotal)}");
            }

            _html.Append("</table>");
            _text.AppendLine();
        }

        public void Totals(IReadOnlyList<(string Label, string Value)> rows, (string Label, string Value)? total)
        {
            _html.Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:0 0 16px 0;\">");
            foreach (var (label, value) in rows)
            {
                _html.Append(
                    $"<tr><td align=\"right\" style=\"padding:3px 6px;font-family:{Sans};font-size:14px;color:{Muted};\">{E(label)}</td>" +
                    $"<td align=\"right\" style=\"padding:3px 6px;font-family:{Sans};font-size:14px;color:{Ink};white-space:nowrap;width:130px;\">{E(value)}</td></tr>");
                _text.AppendLine($"{label}: {value}");
            }

            if (total is { } t)
            {
                _html.Append(
                    $"<tr><td align=\"right\" style=\"padding:8px 6px 3px 6px;font-family:{Serif};font-size:17px;color:{Ink};border-top:1px solid {Line};\">{E(t.Label)}</td>" +
                    $"<td align=\"right\" style=\"padding:8px 6px 3px 6px;font-family:{Serif};font-size:17px;color:{OliveDark};white-space:nowrap;border-top:1px solid {Line};\"><strong>{E(t.Value)}</strong></td></tr>");
                _text.AppendLine($"{t.Label}: {t.Value}");
            }

            _html.Append("</table>");
            _text.AppendLine();
        }

        public void TrackingBox(string groupedCode, string? link, bool firstCode)
        {
            var intro = firstCode
                ? "Sačuvajte ovaj kod: pomoću njega na našem sajtu pratite status porudžbine."
                : "Status porudžbine možete pratiti pomoću ovog koda.";
            _html.Append(
                $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"margin:20px 0 18px 0;\"><tr>" +
                $"<td align=\"center\" style=\"background:{Cream};border:1px solid {Line};border-radius:6px;padding:18px 14px;\">" +
                $"<p style=\"margin:0 0 6px 0;font-family:{Sans};font-size:13px;line-height:1.5;color:{Muted};\">Kod za praćenje porudžbine</p>" +
                $"<p style=\"margin:0 0 10px 0;font-family:'Courier New', Courier, monospace;font-size:19px;line-height:1.4;letter-spacing:1px;color:{Ink};\"><strong>{E(groupedCode)}</strong></p>" +
                $"<p style=\"margin:0 0 14px 0;font-family:{Sans};font-size:13px;line-height:1.5;color:{Muted};\">{E(intro)}</p>");
            _text.AppendLine("Kod za praćenje porudžbine: " + groupedCode).AppendLine(intro);

            if (link is not null)
            {
                _html.Append(Button("Pratite porudžbinu", link));
                _text.AppendLine("Pratite porudžbinu: " + link);
            }

            _html.Append("</td></tr></table>");
            _text.AppendLine();
        }

        public void Link(string label, string url)
        {
            _html.Append($"<p style=\"margin:0 0 14px 0;font-family:{Sans};font-size:14px;line-height:1.6;\"><a href=\"{E(url)}\" style=\"color:{Olive};text-decoration:underline;\">{E(label)}</a></p>");
            _text.AppendLine($"{label}: {url}").AppendLine();
        }

        private static string Button(string label, string url) =>
            "<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" align=\"center\"><tr>" +
            $"<td align=\"center\" bgcolor=\"{Olive}\" style=\"border-radius:4px;\">" +
            $"<a href=\"{E(url)}\" style=\"display:inline-block;padding:12px 26px;font-family:{Sans};font-size:15px;line-height:1.2;color:#FFFFFF;text-decoration:none;border-radius:4px;\"><strong>{E(label)}</strong></a>" +
            "</td></tr></table>";

        public string Html(string subject, string preheader, EmailBranding b, string footer)
        {
            var header = b.LogoUrl is { } logo && IsHttpUrl(logo) && logo.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"<img src=\"{E(logo)}\" alt=\"{E(b.StoreName)}\" width=\"160\" style=\"display:block;border:0;outline:none;max-width:160px;height:auto;margin:0 auto;\">"
                : $"<span style=\"font-family:{Serif};font-size:28px;line-height:1.2;letter-spacing:1px;color:{Olive};\">{E(b.StoreName)}</span>";

            var storeLink = b.StorefrontUrl is { } url && IsHttpUrl(url)
                ? $"<br><a href=\"{E(url)}\" style=\"color:{Muted};text-decoration:underline;\">{E(url)}</a>"
                : string.Empty;

            return
                "<!DOCTYPE html><html lang=\"sr-Latn\" xmlns=\"http://www.w3.org/1999/xhtml\"><head>" +
                "<meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                "<meta name=\"x-apple-disable-message-reformatting\"><meta name=\"color-scheme\" content=\"light\">" +
                $"<title>{E(subject)}</title>" +
                "<style>@media only screen and (max-width:620px){.zl-container{width:100% !important;}.zl-pad{padding-left:20px !important;padding-right:20px !important;}}</style>" +
                "</head>" +
                $"<body style=\"margin:0;padding:0;background:{Cream};-webkit-text-size-adjust:100%;-ms-text-size-adjust:100%;\">" +
                $"<div style=\"display:none;max-height:0;overflow:hidden;mso-hide:all;font-size:1px;line-height:1px;color:{Cream};\">{E(preheader)}</div>" +
                $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" bgcolor=\"{Cream}\" style=\"background:{Cream};\"><tr><td align=\"center\" style=\"padding:24px 10px;\">" +
                $"<table role=\"presentation\" class=\"zl-container\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" style=\"width:600px;max-width:600px;\">" +
                $"<tr><td align=\"center\" style=\"padding:8px 0 20px 0;\">{header}</td></tr>" +
                $"<tr><td bgcolor=\"{Card}\" style=\"background:{Card};border:1px solid {Line};border-top:4px solid {Olive};border-radius:6px;\">" +
                $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\"><tr><td class=\"zl-pad\" style=\"padding:32px 36px 28px 36px;\">" +
                _html +
                "</td></tr></table></td></tr>" +
                $"<tr><td class=\"zl-pad\" align=\"center\" style=\"padding:20px 36px 8px 36px;font-family:{Sans};font-size:12px;line-height:1.6;color:{Muted};\">" +
                $"{E(footer)}{storeLink}</td></tr>" +
                "</table></td></tr></table></body></html>";
        }

        public string Text(string subject, EmailBranding b, string footer)
        {
            var builder = new StringBuilder();
            builder.AppendLine(b.StoreName).AppendLine();
            builder.Append(_text);
            builder.AppendLine("--");
            builder.AppendLine(footer);
            if (b.StorefrontUrl is { } url && IsHttpUrl(url))
            {
                builder.AppendLine(url);
            }

            return builder.ToString().ReplaceLineEndings("\r\n");
        }
    }
}
