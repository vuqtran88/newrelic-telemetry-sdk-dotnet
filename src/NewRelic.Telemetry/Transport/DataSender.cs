﻿using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace NewRelic.Telemetry.Transport
{
    public abstract class DataSender<TData> where TData : ITelemetryDataType
    {
        protected readonly TelemetryConfiguration _config;
        protected readonly TelemetryLogging _logger;

        private Func<string, Task<HttpResponseMessage>> _httpHandlerDelegate;

        private const string _userAgent = "NewRelic-Dotnet-TelemetrySDK";
        private const string _implementationVersion = "/1.0.0";
        private HttpClient _httpClient;

        private Func<int, Task> _delayer;
        private static readonly Func<int, Task> _defaultDelayer = new Func<int, Task>(async (int milliseconds) => await Task.Delay(milliseconds));

        protected abstract string EndpointUrl { get; }

        protected abstract TData[] Split(TData dataToSplit);

        protected abstract bool ContainsNoData(TData dataToCheck);

        internal DataSender<TData> WithDelayFunction(Func<int, Task> delayerImpl)
        {
            _delayer = delayerImpl;
            return this;
        }

        internal void WithHttpHandlerImpl(Func<string, Task<HttpResponseMessage>> httpHandler)
        {
            _httpHandlerDelegate = httpHandler;
        }


        protected DataSender(TelemetryConfiguration config) 
        {
            _config = config;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(_config.SendTimeout);

            var sp = ServicePointManager.FindServicePoint(new Uri(EndpointUrl));
            sp.ConnectionLeaseTimeout = 60000;  // 1 minute

            _httpHandlerDelegate = SendDataAsync;
        }

        protected DataSender(TelemetryConfiguration config, TelemetryLogging logger) : this(config)
        {
            _logger = logger;
        }


        protected DataSender(IConfiguration configProvider) : this(new TelemetryConfiguration(configProvider))
        {
        }

        protected DataSender(IConfiguration configProvider, TelemetryLogging logger) : this(configProvider)
        {
            _logger = logger;
        }

        
        private async Task<Response> RetryWithSplit(TData data)
        {
            var newBatches = Split(data);

            if (newBatches == null)
            {
                _logger?.Error($@"Cannot send data because it exceeds the size limit and cannot be split.");
                return Response.ResponseFailure;
            }

            _logger?.Warning("Splitting the data and retrying.");

            var taskList = new Task<Response>[newBatches.Length];

            for (var i = 0; i < newBatches.Length; i++)
            {
                taskList[i] = SendDataAsync(newBatches[i]);
            }
            
            var responses = await Task.WhenAll(taskList);

            if(responses.All(x=>x.ResponseStatus == NewRelicResponseStatus.SendSuccess))
            {
                return Response.Success;
            }

            return Response.ResponseFailure;
        }
 
        private async Task<Response> RetryWithDelay(TData data, int retryNum, int? waitTimeInSeconds = null)
        {
            retryNum++;
            if (retryNum > _config.MaxRetryAttempts)
            {
                _logger?.Error($@"Send Data failed after {_config.MaxRetryAttempts} attempts.");
                return Response.ResponseFailure;
            }

            waitTimeInSeconds = waitTimeInSeconds ?? (int)Math.Min(_config.BackoffMaxSeconds, _config.BackoffDelayFactorSeconds * Math.Pow(2, retryNum - 1));

            _logger?.Warning($@"Attempting retry({retryNum}) after {waitTimeInSeconds} seconds.");

            await (_delayer ?? _defaultDelayer)(waitTimeInSeconds.Value * 1000);

            var result = await SendDataAsync(data, retryNum);
            return result;
        }

        private async Task<Response> RetryWithServerDelay(TData dataToSend, int retryNum, HttpResponseMessage httpResponse)
        {
            retryNum++;
            if (retryNum > _config.MaxRetryAttempts)
            {
                _logger?.Error($@"Send Data failed after {_config.MaxRetryAttempts} attempts.");
                return Response.ResponseFailure;
            }

            var retryAfterDelay = httpResponse.Headers?.RetryAfter?.Delta;
            var retryAtSpecificDate = httpResponse.Headers?.RetryAfter?.Date;

            if (!retryAfterDelay.HasValue && retryAtSpecificDate.HasValue)
            {
                retryAfterDelay = retryAtSpecificDate - DateTimeOffset.UtcNow;
            }

            var delayMs = (int)retryAfterDelay.Value.TotalMilliseconds;

            //Perform the delay using the waiter delegate
            await (_delayer ?? _defaultDelayer)(delayMs);

            return await SendDataAsync(dataToSend, retryNum);
        }

        public async Task<Response> SendDataAsync(TData dataToSend)
        {

            if(string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                _logger.Exception(new ArgumentNullException("Configuration requires API key"));
                return Response.ResponseFailure;
            }

            return await SendDataAsync(dataToSend, 0);
        }

        private async Task<Response> SendDataAsync(TData dataToSend, int retryNum)
        {
            if(ContainsNoData(dataToSend))
            {
                return Response.ResponseDidNotSend;
            }

            var serializedPayload = dataToSend.ToJson();

            var httpResponse = await _httpHandlerDelegate(serializedPayload);

            switch (httpResponse.StatusCode)
            {
                //Success
                case HttpStatusCode code when code >= HttpStatusCode.OK && code <= (HttpStatusCode)299:
                    _logger?.Debug($@"Response from New Relic ingest API: code: {httpResponse.StatusCode}");
                    return Response.Success;

                case HttpStatusCode.BadRequest:
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NotFound:
                case HttpStatusCode.MethodNotAllowed:
                case HttpStatusCode.LengthRequired:
                    _logger?.Error($@"Response from New Relic ingest API: code: {httpResponse.StatusCode}");
                    return Response.ResponseFailure;

                case HttpStatusCode.RequestEntityTooLarge:
                    _logger?.Warning($@"Response from New Relic ingest API: code: {httpResponse.StatusCode}. Response indicates payload is too large.");
                    return await RetryWithSplit(dataToSend);

                case HttpStatusCode.RequestTimeout:
                    _logger?.Warning($@"Response from New Relic ingest API: code: {httpResponse.StatusCode}");
                    return await RetryWithDelay(dataToSend, retryNum);

                case (HttpStatusCode)429:
                    _logger?.Warning($@"Response from New Relic ingest API: code: {httpResponse.StatusCode}. ");
                    return await RetryWithServerDelay(dataToSend, retryNum, httpResponse);
                
                default:
                    _logger?.Error($@"Response from New Relic ingest API: code: {httpResponse.StatusCode}");
                    return Response.ResponseFailure;
            }
        }

        private async Task<HttpResponseMessage> SendDataAsync(string serializedPayload)
        {
            var serializedBytes = new UTF8Encoding().GetBytes(serializedPayload);

            using (var memoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(memoryStream, CompressionMode.Compress, true))
                {
                    gzipStream.Write(serializedBytes, 0, serializedBytes.Length);
                }

                memoryStream.Position = 0;

                var streamContent = new StreamContent(memoryStream);
                streamContent.Headers.Add("Content-Type", "application/json; charset=utf-8");
                streamContent.Headers.Add("Content-Encoding", "gzip");
                streamContent.Headers.ContentLength = memoryStream.Length;

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
                requestMessage.Content = streamContent;
                requestMessage.Headers.Add("User-Agent", _userAgent + _implementationVersion);
                requestMessage.Headers.Add("Api-Key", _config.ApiKey);
                requestMessage.Method = HttpMethod.Post;

                var response = await _httpClient.SendAsync(requestMessage);

                if (_config.AuditLoggingEnabled)
                {
                    _logger?.Debug($@"Sent payload: '{serializedPayload}'");
                }

                return response;
            }
        }
    }
}
