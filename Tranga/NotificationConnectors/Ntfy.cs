﻿using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Tranga.NotificationConnectors;

public class Ntfy : NotificationConnector
{
    // ReSharper disable twice MemberCanBePrivate.Global
    public string endpoint { get; init; }
    public string auth { get; init; }
    public string topic { get; init; }
    private readonly HttpClient _client = new();

    [JsonConstructor]
    public Ntfy(GlobalBase clone, string endpoint, string topic, string auth) : base(clone, NotificationConnectorType.Ntfy)
    {
        this.endpoint = endpoint;
        this.topic = topic;
        this.auth = auth;
    }
    
    public Ntfy(GlobalBase clone, string endpoint, string username, string password, string? topic = null) : 
        this(clone, EndpointAndTopicFromUrl(endpoint)[0], topic??EndpointAndTopicFromUrl(endpoint)[1], AuthFromUsernamePassword(username, password))
    {
        
    }

    private static string AuthFromUsernamePassword(string username, string password)
    {
        string authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        string authParam = Convert.ToBase64String(Encoding.UTF8.GetBytes(authHeader)).Replace("=","");
        return authParam;
    }

    private static string[] EndpointAndTopicFromUrl(string url)
    {
        string[] ret = new string[2];
        if (!baseUrlRex.IsMatch(url))
            throw new ArgumentException("url does not match pattern");
        Regex rootUriRex = new(@"(https?:\/\/[a-zA-Z0-9-\.]+\.[a-zA-Z0-9]+)(?:\/([a-zA-Z0-9-\.]+))?.*");
        Match match = rootUriRex.Match(url);
        if(!match.Success)
            throw new ArgumentException($"Error getting URI from provided endpoint-URI: {url}");
        
        ret[0] = match.Groups[1].Value;
        ret[1] = match.Groups[2].Success && match.Groups[2].Value.Length > 0 ? match.Groups[2].Value : "tranga";

        return ret;
    }

    public override string ToString()
    {
        return $"Ntfy {endpoint} {topic}";
    }

    protected override void SendNotificationInternal(string title, string notificationText)
    {
        Log($"Sending notification: {title} - {notificationText}");
        MessageData message = new(title, topic, notificationText);
        HttpRequestMessage request = new(HttpMethod.Post, $"{endpoint}");
        request.Headers.Add("Authorization", $"{auth}");
        request.Content = new StringContent(JsonConvert.SerializeObject(message, Formatting.None), Encoding.UTF8, "application/json");
        HttpResponseMessage response = _client.Send(request);
        if (!response.IsSuccessStatusCode)
        {
            StreamReader sr = new (response.Content.ReadAsStream());
            Log($"{response.StatusCode}: {sr.ReadToEnd()}");
        }
    }

    private class MessageData
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public string topic { get; }
        public string title { get; }
        public string message { get; }
        public int priority { get; }

        public MessageData(string title, string topic, string message)
        {
            this.topic = topic;
            this.title = title;
            this.message = message;
            this.priority = 3;
        }
    }
}