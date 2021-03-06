﻿using NewRelic.Telemetry;
using NewRelic.Telemetry.Transport;
using NRSpans = NewRelic.Telemetry.Spans;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Linq;
using OpenTelemetry.Trace.Export;
using OpenTelemetry.Trace;
using System.Reflection;

namespace OpenTelemetry.Exporter.NewRelic
{
    /// <summary>
    /// An exporter used to send Trace/Span information to New Relic.
    /// </summary>
    public class NewRelicTraceExporter : SpanExporter
    {
        private readonly NRSpans.SpanDataSender _spanDataSender;
        private const string _productName = "OpenTelemetry.Exporter.NewRelic";
        private static readonly string _productVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<PackageVersionAttribute>().PackageVersion;

        private const string _attribName_url = "http.url";

        private readonly ILogger _logger;
        private readonly TelemetryConfiguration _config;
        private readonly string[] _nrEndpoints;


        /// <summary>
        /// Configures the Trace Exporter accepting settings from any configuration provider supported by Microsoft.Extensions.Configuration.
        /// </summary>
        /// <param name="configProvider"></param>
        public NewRelicTraceExporter(IConfiguration configProvider) : this(configProvider, null)
        {
        }

        /// <summary>
        /// Configures the Trace Exporter accepting settings from any configuration provider supported by Microsoft.Extensions.Configuration.
        /// Also accepts any logging infrastructure supported by Microsoft.Extensions.Logging.
        /// </summary>
        /// <param name="configProvider"></param>
        /// <param name="loggerFactory"></param>
        public NewRelicTraceExporter(IConfiguration configProvider, ILoggerFactory loggerFactory) : this(new TelemetryConfiguration(configProvider), loggerFactory)
        {
        }

        /// <summary>
        /// Configures the Trace Exporter accepting configuration settings from an instance of the New Relic Telemetry SDK configuration object.
        /// </summary>
        /// <param name="config"></param>
        public NewRelicTraceExporter(TelemetryConfiguration config) : this(config, null)
        {
        }

        /// <summary>
        /// Configures the Trace Exporter accepting configuration settings from an instance of the New Relic Telemetry SDK configuration object.  Also
        /// accepts a logger factory supported by Microsoft.Extensions.Logging.
        /// </summary>
        /// <param name="config"></param>
        public NewRelicTraceExporter(TelemetryConfiguration config, ILoggerFactory loggerFactory) : this(new NRSpans.SpanDataSender(config, loggerFactory),config,loggerFactory)
        {
        }

        internal NewRelicTraceExporter(NRSpans.SpanDataSender spanDataSender, TelemetryConfiguration config, ILoggerFactory loggerFactory)
        {
            _spanDataSender = spanDataSender;
            spanDataSender.AddVersionInfo(_productName, _productVersion);

            _config = config;

            _nrEndpoints = config.NewRelicEndpoints.Select(x => x.ToLower()).ToArray();

            if (loggerFactory != null)
            {
                _logger = loggerFactory.CreateLogger("NewRelicTraceExporter");
            }
        }


        /// <summary>
        /// Responsible for sending Open Telemetry Spans to New Relic endpoint.
        /// </summary>
        /// <param name="otSpans">Collection of Open Telemetry spans to be sent to New Relic</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async override Task<ExportResult> ExportAsync(IEnumerable<Span> otSpans, CancellationToken cancellationToken)
        {
            if (otSpans == null) throw new ArgumentNullException(nameof(otSpans));
            if (cancellationToken == null) throw new ArgumentNullException(nameof(cancellationToken));

            var nrSpanBatch = ToNewRelicSpanBatch(otSpans);

            if(nrSpanBatch.Spans.Count == 0)
            {
                return ExportResult.Success;
            }

            var result = await _spanDataSender.SendDataAsync(nrSpanBatch);

            switch (result.ResponseStatus)
            {
                case NewRelicResponseStatus.DidNotSend_NoData:
                case NewRelicResponseStatus.Success:
                    return ExportResult.Success;
               
                case NewRelicResponseStatus.Failure:
                default:
                    return ExportResult.FailedRetryable;
            }
        }

        public override Task ShutdownAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public void Dispose()
        {
        }

        private NRSpans.SpanBatch ToNewRelicSpanBatch(IEnumerable<Span> otSpans)
        {
            var nrSpans = new List<NRSpans.Span>();
            var spanIdsToFilter = new List<string>();

            foreach (var otSpan in otSpans)
            {
                if(otSpan == null)
                {
                    continue;
                }

                try
                {
                    var nrSpan = ToNewRelicSpan(otSpan);
                    if(nrSpan == null)
                    {
                        spanIdsToFilter.Add(otSpan.Context.SpanId.ToHexString());
                        _logger?.LogDebug(null, $"The following span was filtered because it describes communication with a New Relic endpoint: Trace={otSpan.Context.TraceId}, Span={otSpan.Context.SpanId}, ParentSpan={otSpan.ParentSpanId}");
                    }
                    else
                    {
                        nrSpans.Add(nrSpan);
                    }
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                    {
                        var otSpanId = "<unknown>";
                        try
                        {
                            otSpanId = otSpan.Context.SpanId.ToHexString();
                        }
                        catch { }

                        _logger.LogError(null, ex, $"Error translating Open Telemetry Span {otSpanId} to New Relic Span.");
                    }
                }
            }

            nrSpans = FilterSpans(nrSpans, spanIdsToFilter);

            var spanBatchBuilder = NRSpans.SpanBatchBuilder.Create();

            spanBatchBuilder.WithSpans(nrSpans);

            var nrSpanBatch = spanBatchBuilder.Build();

            return nrSpanBatch;
        }

        private List<NRSpans.Span> FilterSpans(List<NRSpans.Span> spans, List<string> spanIdsToFilter)
        {
            if(spanIdsToFilter.Count == 0)
            {
                return spans;
            }

            var newSpansToFilter = spans.Where(x => spanIdsToFilter.Contains(x.ParentId)).ToArray();
            
            if (newSpansToFilter.Length == 0)
            {
                return spans;
            } 
            
            if (_logger?.IsEnabled(LogLevel.Debug) == true)
            {
                foreach (var spanToFilter in newSpansToFilter)
                {
                    _logger?.LogDebug(null, $"The following span was filtered because it is a descendant of a span that describes communication with a New Relic endpoint: Trace={spanToFilter.TraceId}, Span={spanToFilter.Id}, ParentSpan={spanToFilter.ParentId ?? "<NULL>"}");
                }
            }

            var newSpanIdsToFilter = newSpansToFilter.Select(x=>x.Id).ToArray();

            spanIdsToFilter = spanIdsToFilter.Union(newSpanIdsToFilter).ToList();

            spans = spans.Except(newSpansToFilter).ToList();
            
            return FilterSpans(spans, spanIdsToFilter);
        }

        private NRSpans.Span ToNewRelicSpan(Span openTelemetrySpan)
        {
            if (openTelemetrySpan == null) throw new ArgumentNullException(nameof(openTelemetrySpan));
            if (openTelemetrySpan.Context == null) throw new NullReferenceException($"{nameof(openTelemetrySpan)}.Context");
            if (openTelemetrySpan.Context.SpanId == null) throw new NullReferenceException($"{nameof(openTelemetrySpan)}.Context.SpanId");
            if (openTelemetrySpan.Context.TraceId == null) throw new NullReferenceException($"{nameof(openTelemetrySpan)}.Context.TraceId");
            if (openTelemetrySpan.StartTimestamp == null) throw new NullReferenceException($"{nameof(openTelemetrySpan)}.StartTimestamp");

            var newRelicSpanBuilder = NRSpans.SpanBuilder.Create(openTelemetrySpan.Context.SpanId.ToHexString())
                   .WithTraceId(openTelemetrySpan.Context.TraceId.ToHexString())
                   .WithExecutionTimeInfo(openTelemetrySpan.StartTimestamp, openTelemetrySpan.EndTimestamp)   //handles Nulls
                   .HasError(!openTelemetrySpan.Status.IsOk)
                   .WithName(openTelemetrySpan.Name);       //Handles Nulls

            if (!string.IsNullOrWhiteSpace(_config.ServiceName))
            {
                newRelicSpanBuilder.WithServiceName(_config.ServiceName);
            }

            if (openTelemetrySpan.ParentSpanId != null && openTelemetrySpan.ParentSpanId != default)
            {
                newRelicSpanBuilder.WithParentId(openTelemetrySpan.ParentSpanId.ToHexString());
            }

            if (openTelemetrySpan.Attributes != null)
            {
                foreach (var spanAttrib in openTelemetrySpan.Attributes)
                {
                    //Filter out calls to New Relic endpoint as these will cause an infinite loop
                    if (string.Equals(spanAttrib.Key, _attribName_url, StringComparison.OrdinalIgnoreCase) && _nrEndpoints.Contains(spanAttrib.Value?.ToString().ToLower()))
                    {
                        _logger?.LogDebug(null, $"The following span was filtered because it was identified as communication with a New Relic endpoint: Trace={openTelemetrySpan.Context.TraceId}, Span={openTelemetrySpan.Context.SpanId}, ParentSpan={openTelemetrySpan.ParentSpanId}. url={spanAttrib.Value}");

                        return null;
                    }

                    newRelicSpanBuilder.WithAttribute(spanAttrib.Key, spanAttrib.Value);
                }
            }

            return newRelicSpanBuilder.Build();
        }
    }
}
