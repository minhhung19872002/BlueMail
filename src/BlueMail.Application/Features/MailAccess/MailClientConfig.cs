using System.Security;
using BlueMail.Infrastructure.Configuration;

namespace BlueMail.Application.Features.MailAccess;

/// <summary>
/// Automatic set-up of mail programs (PROMPT 7.2, 13 phase 11): Thunderbird's autoconfig (config-v1.1.xml, served at
/// autoconfig.&lt;domain&gt; and /.well-known/autoconfig) and Outlook's autodiscover (POX). The domain records point here
/// (DomainRecordPlanner: autoconfig / autodiscover CNAMEs, RFC 6186 SRV).
/// </summary>
public sealed class MailClientConfig(BmSettings settings)
{
    private string Platform => settings.MailHostname.Contains('.', StringComparison.Ordinal)
        ? settings.MailHostname[(settings.MailHostname.IndexOf('.', StringComparison.Ordinal) + 1)..] : settings.MailHostname;

    public string ImapHost => "imap." + Platform;
    public string SmtpHost => "smtp." + Platform;

    public string Thunderbird(string? email)
    {
        var domain = email is { } e && e.Contains('@', StringComparison.Ordinal) ? e[(e.LastIndexOf('@') + 1)..] : Platform;
        string X(string s) => SecurityElement.Escape(s);
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <clientConfig version="1.1">
              <emailProvider id="{X(domain)}">
                <domain>{X(domain)}</domain>
                <displayName>BlueMail</displayName>
                <displayShortName>BlueMail</displayShortName>
                <incomingServer type="imap">
                  <hostname>{X(ImapHost)}</hostname>
                  <port>993</port>
                  <socketType>SSL</socketType>
                  <authentication>password-cleartext</authentication>
                  <username>%EMAILADDRESS%</username>
                </incomingServer>
                <outgoingServer type="smtp">
                  <hostname>{X(SmtpHost)}</hostname>
                  <port>465</port>
                  <socketType>SSL</socketType>
                  <authentication>password-cleartext</authentication>
                  <username>%EMAILADDRESS%</username>
                </outgoingServer>
              </emailProvider>
            </clientConfig>
            """;
    }

    /// <summary>Outlook autodiscover response (POX schema 2006a) for the e-mail address in the request.</summary>
    public string Outlook(string? email)
    {
        string X(string s) => SecurityElement.Escape(s);
        var user = X(email ?? "");
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Autodiscover xmlns="http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006">
              <Response xmlns="http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a">
                <Account>
                  <AccountType>email</AccountType>
                  <Action>settings</Action>
                  <Protocol>
                    <Type>IMAP</Type>
                    <Server>{X(ImapHost)}</Server>
                    <Port>993</Port>
                    <LoginName>{user}</LoginName>
                    <SPA>off</SPA>
                    <SSL>on</SSL>
                    <AuthRequired>on</AuthRequired>
                  </Protocol>
                  <Protocol>
                    <Type>SMTP</Type>
                    <Server>{X(SmtpHost)}</Server>
                    <Port>465</Port>
                    <LoginName>{user}</LoginName>
                    <SPA>off</SPA>
                    <Encryption>SSL</Encryption>
                    <AuthRequired>on</AuthRequired>
                  </Protocol>
                </Account>
              </Response>
            </Autodiscover>
            """;
    }

    /// <summary>The EMailAddress element of an Outlook autodiscover request (small XML, parsed without DTDs).</summary>
    public static string? EmailFromOutlookRequest(Stream body)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(body, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, MaxCharactersInDocument = 64 * 1024 });
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.LocalName == "EMailAddress")
                {
                    return reader.ReadElementContentAsString().Trim();
                }
            }
        }
        catch (System.Xml.XmlException)
        {
            // not a valid request: answer with the generic settings
        }

        return null;
    }
}
