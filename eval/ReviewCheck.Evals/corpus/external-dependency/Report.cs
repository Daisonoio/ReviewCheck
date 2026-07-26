namespace Sample.Reporting;

/// <summary>Builds a summary and hands it to a mail gateway defined outside this change.</summary>
public class Report
{
    /// <summary>Formats the summary and delivers it through the external mailer.</summary>
    public void Send(string summary)
    {
        var payload = "REPORT: " + summary;
        ExternalMailer.Deliver(payload);
    }
}
