﻿using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Tranga.MangaConnectors;

internal class ChromiumDownloadClient : DownloadClient
{
    private static IBrowser? _browser;
    private readonly HttpDownloadClient _httpDownloadClient;
    
    private static async Task<IBrowser> StartBrowser(Logging.Logger? logger = null)
    {
        logger?.WriteLine("Starting ChromiumDownloadClient Puppeteer");
        return await Puppeteer.LaunchAsync(new LaunchOptions
        {
            Headless = true,
            Args = new [] {
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--disable-setuid-sandbox",
                "--no-sandbox"},
            Timeout = TrangaSettings.ChromiumStartupTimeoutMs
        }, new LoggerFactory([new LogProvider(logger)]));
    }

    private class LogProvider : GlobalBase, ILoggerProvider
    {
        public LogProvider(Logging.Logger? logger) : base(logger) { }

        public void Dispose() { }

        public ILogger CreateLogger(string categoryName) => new Logger(logger);
    }

    private class Logger : GlobalBase, ILogger
    {
        public Logger(Logging.Logger? logger) : base(logger) { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel <= LogLevel.Information)
                return;
            logger?.WriteLine("Puppeteer", formatter.Invoke(state, exception));
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    public ChromiumDownloadClient(GlobalBase clone) : base(clone)
    {
        _httpDownloadClient = new(this);
        if(_browser is null)
            _browser = StartBrowser(this.logger).Result;
    }

    private readonly Regex _imageUrlRex = new(@"https?:\/\/.*\.(?:p?jpe?g|gif|a?png|bmp|avif|webp)(\?.*)?");
    internal override RequestResult MakeRequestInternal(string url, string? referrer = null, string? clickButton = null)
    {
        return _imageUrlRex.IsMatch(url)
            ? _httpDownloadClient.MakeRequestInternal(url, referrer)
            : MakeRequestBrowser(url, referrer, clickButton);
    }

    private RequestResult MakeRequestBrowser(string url, string? referrer = null, string? clickButton = null)
    {
        if (_browser is null)
            return new RequestResult(HttpStatusCode.InternalServerError, null, Stream.Null);
        IPage page = _browser.NewPageAsync().Result;
        page.DefaultTimeout = TrangaSettings.ChromiumPageTimeoutMs;
        page.SetExtraHttpHeadersAsync(new() { { "Referer", referrer } });
        IResponse response;
        try
        {
            response = page.GoToAsync(url, WaitUntilNavigation.Networkidle0).Result;
            Log($"Page loaded. {url}");
        }
        catch (Exception e)
        {
            Log($"Could not load Page {url}\n{e.Message}");
            page.CloseAsync();
            return new RequestResult(HttpStatusCode.InternalServerError, null, Stream.Null);
        }

        Stream stream = Stream.Null;
        HtmlDocument? document = null;

        if (response.Headers.TryGetValue("Content-Type", out string? content))
        {
            if (content.Contains("text/html"))
            {
                if (clickButton is not null && page.QuerySelectorAsync(clickButton).Result is not null)
                    page.ClickAsync(clickButton).Wait();
                string htmlString = page.GetContentAsync().Result;
                stream = new MemoryStream(Encoding.Default.GetBytes(htmlString));
                document = new ();
                document.LoadHtml(htmlString);
            }else if (content.Contains("image"))
            {
                stream = new MemoryStream(response.BufferAsync().Result);
            }
        }
        else
        {
            page.CloseAsync();
            return new RequestResult(HttpStatusCode.InternalServerError, null, Stream.Null);
        }
        
        page.CloseAsync();
        return new RequestResult(response.Status, document, stream, false, "");
    }
}