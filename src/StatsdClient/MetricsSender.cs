﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using StatsdClient.Bufferize;

namespace StatsdClient
{
    internal class MetricsSender
    {
        private const string _entityIdInternalTagKey = "dd.internal.entity_id";
        private static readonly string[] EmptyStringArray = new string[0];
        private readonly string _prefix;
        private readonly string[] _constantTags;
        private readonly Telemetry _optionalTelemetry;
        private List<string> _commands = new List<string>();
        private readonly StatsBufferize _statsBufferize;

        internal MetricsSender(
                    StatsBufferize statsBufferize,
                    IRandomGenerator randomGenerator,
                    IStopWatchFactory stopwatchFactory,
                    string prefix,
                    string[] constantTags,
                    Telemetry optionalTelemetry)
        {
            StopwatchFactory = stopwatchFactory;
            _statsBufferize = statsBufferize;
            RandomGenerator = randomGenerator;
            _prefix = prefix;
            _optionalTelemetry = optionalTelemetry;

            string entityId = Environment.GetEnvironmentVariable(StatsdConfig.DD_ENTITY_ID_ENV_VAR);

            if (string.IsNullOrEmpty(entityId))
            {
                // copy array to prevent changes, coalesce to empty array
                _constantTags = constantTags?.ToArray() ?? EmptyStringArray;
            }
            else
            {
                var entityIdTags = new[] { $"{_entityIdInternalTagKey}:{entityId}" };
                _constantTags = constantTags == null ? entityIdTags : constantTags.Concat(entityIdTags).ToArray();
            }
        }

        public bool TruncateIfTooLong { get; set; }

        public List<string> Commands
        {
            get { return _commands; }
            private set { _commands = value; }
        }

        private IStopWatchFactory StopwatchFactory { get; set; }

        private IRandomGenerator RandomGenerator { get; set; }

        public void Send(string title, string text, string alertType = null, string aggregationKey = null, string sourceType = null, int? dateHappened = null, string priority = null, string hostname = null, string[] tags = null, bool truncateIfTooLong = false)
        {
            truncateIfTooLong = truncateIfTooLong || TruncateIfTooLong;
            Send(Event.GetCommand(title, text, alertType, aggregationKey, sourceType, dateHappened, priority, hostname, _constantTags, tags, truncateIfTooLong));
            _optionalTelemetry?.OnEventSent();
        }

        public void Send(string name, int status, int? timestamp = null, string hostname = null, string[] tags = null, string serviceCheckMessage = null, bool truncateIfTooLong = false)
        {
            truncateIfTooLong = truncateIfTooLong || TruncateIfTooLong;
            Send(ServiceCheck.GetCommand(name, status, timestamp, hostname, _constantTags, tags, serviceCheckMessage, truncateIfTooLong));
            _optionalTelemetry?.OnServiceCheckSent();
        }

        public void Send<TCommandType, T>(string name, T value, double sampleRate = 1.0, string[] tags = null)
            where TCommandType : Metric
        {
            if (RandomGenerator.ShouldSend(sampleRate))
            {
                Send(Metric.GetCommand<TCommandType, T>(_prefix, name, value, sampleRate, _constantTags, tags));
                _optionalTelemetry?.OnMetricSent();
            }
        }

        public void Send(string command)
        {
            try
            {
                // clear buffer (keep existing behavior)
                if (Commands.Count > 0)
                {
                    Commands = new List<string>();
                }

                 _statsBufferize.Send(command);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
        }

        public void Send()
        {
            int count = Commands.Count;
            if (count < 1)
            {
                return;
            }

            Send(count == 1 ? Commands[0] : string.Join("\n", Commands.ToArray()));
        }

        public void Send(Action actionToTime, string statName, double sampleRate = 1.0, string[] tags = null)
        {
            var stopwatch = StopwatchFactory.Get();

            try
            {
                stopwatch.Start();
                actionToTime();
            }
            finally
            {
                stopwatch.Stop();
                Send<Timing, int>(statName, stopwatch.ElapsedMilliseconds(), sampleRate, tags);
            }
        }

        private static string EscapeContent(string content)
        {
            return content
                .Replace("\r", string.Empty)
                .Replace("\n", "\\n");
        }

        private static string ConcatTags(string[] constantTags, string[] tags)
        {
            // avoid dealing with null arrays
            constantTags = constantTags ?? EmptyStringArray;
            tags = tags ?? EmptyStringArray;

            if (constantTags.Length == 0 && tags.Length == 0)
            {
                return string.Empty;
            }

            var allTags = constantTags.Concat(tags);
            string concatenatedTags = string.Join(",", allTags);
            return $"|#{concatenatedTags}";
        }

        private static string TruncateOverage(string str, int overage)
        {
            return str.Substring(0, str.Length - overage);
        }

        public class Counting : Metric
        {
        }

        public class Timing : Metric
        {
        }

        public class Gauge : Metric
        {
        }

        public class Histogram : Metric
        {
        }

        public class Distribution : Metric
        {
        }

        public class Meter : Metric
        {
        }

        public class Set : Metric
        {
        }

        public abstract class Metric : ICommandType
        {
            private static readonly Dictionary<Type, string> _commandToUnit = new Dictionary<Type, string>
                                                                {
                                                                    { typeof(Counting), "c" },
                                                                    { typeof(Timing), "ms" },
                                                                    { typeof(Gauge), "g" },
                                                                    { typeof(Histogram), "h" },
                                                                    { typeof(Distribution), "d" },
                                                                    { typeof(Meter), "m" },
                                                                    { typeof(Set), "s" },
                                                                };

            public static string GetCommand<TCommandType, T>(string prefix, string name, T value, double sampleRate, string[] tags)
                where TCommandType : Metric
            {
                return GetCommand<TCommandType, T>(prefix, name, value, sampleRate, null, tags);
            }

            public static string GetCommand<TCommandType, T>(string prefix, string name, T value, double sampleRate, string[] constantTags, string[] tags)
                where TCommandType : Metric
            {
                string full_name = prefix + name;
                string unit = _commandToUnit[typeof(TCommandType)];
                var allTags = ConcatTags(constantTags, tags);

                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}:{1}|{2}{3}{4}",
                    full_name,
                    value,
                    unit,
                    sampleRate == 1.0 ? string.Empty : string.Format(CultureInfo.InvariantCulture, "|@{0}", sampleRate),
                    allTags);
            }
        }

        public class Event : ICommandType
        {
            private const int MaxSize = 8 * 1024;

            public static string GetCommand(string title, string text, string alertType, string aggregationKey, string sourceType, int? dateHappened, string priority, string hostname, string[] tags, bool truncateIfTooLong = false)
            {
                return GetCommand(title, text, alertType, aggregationKey, sourceType, dateHappened, priority, hostname, null, tags, truncateIfTooLong);
            }

            public static string GetCommand(string title, string text, string alertType, string aggregationKey, string sourceType, int? dateHappened, string priority, string hostname, string[] constantTags, string[] tags, bool truncateIfTooLong = false)
            {
                string processedTitle = EscapeContent(title);
                string processedText = EscapeContent(text);
                string result = string.Format(CultureInfo.InvariantCulture, "_e{{{0},{1}}}:{2}|{3}", processedTitle.Length.ToString(), processedText.Length.ToString(), processedTitle, processedText);
                if (dateHappened != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|d:{0}", dateHappened);
                }

                if (hostname != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|h:{0}", hostname);
                }

                if (aggregationKey != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|k:{0}", aggregationKey);
                }

                if (priority != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|p:{0}", priority);
                }

                if (sourceType != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|s:{0}", sourceType);
                }

                if (alertType != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|t:{0}", alertType);
                }

                result += ConcatTags(constantTags, tags);

                if (result.Length > MaxSize)
                {
                    if (truncateIfTooLong)
                    {
                        var overage = result.Length - MaxSize;
                        if (title.Length > text.Length)
                        {
                            title = TruncateOverage(title, overage);
                        }
                        else
                        {
                            text = TruncateOverage(text, overage);
                        }

                        return GetCommand(title, text, alertType, aggregationKey, sourceType, dateHappened, priority, hostname, tags, true);
                    }
                    else
                    {
                        throw new Exception(string.Format("Event {0} payload is too big (more than 8kB)", title));
                    }
                }

                return result;
            }
        }

        public class ServiceCheck : ICommandType
        {
            private const int MaxSize = 8 * 1024;

            public static string GetCommand(string name, int status, int? timestamp, string hostname, string[] tags, string serviceCheckMessage, bool truncateIfTooLong = false)
            {
                return GetCommand(name, status, timestamp, hostname, null, tags, serviceCheckMessage, truncateIfTooLong);
            }

            public static string GetCommand(string name, int status, int? timestamp, string hostname, string[] constantTags, string[] tags, string serviceCheckMessage, bool truncateIfTooLong = false)
            {
                string processedName = EscapeName(name);
                string processedMessage = EscapeMessage(serviceCheckMessage);

                string result = string.Format(CultureInfo.InvariantCulture, "_sc|{0}|{1}", processedName, status);

                if (timestamp != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|d:{0}", timestamp);
                }

                if (hostname != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|h:{0}", hostname);
                }

                result += ConcatTags(constantTags, tags);

                // Note: this must always be appended to the result last.
                if (processedMessage != null)
                {
                    result += string.Format(CultureInfo.InvariantCulture, "|m:{0}", processedMessage);
                }

                if (result.Length > MaxSize)
                {
                    if (!truncateIfTooLong)
                    {
                        throw new Exception(string.Format("ServiceCheck {0} payload is too big (more than 8kB)", name));
                    }

                    var overage = result.Length - MaxSize;

                    if (processedMessage == null || overage > processedMessage.Length)
                    {
                        throw new ArgumentException(string.Format("ServiceCheck name is too long to truncate, payload is too big (more than 8Kb) for {0}", name), "name");
                    }

                    var truncMessage = TruncateOverage(processedMessage, overage);
                    return GetCommand(name, status, timestamp, hostname, tags, truncMessage, true);
                }

                return result;
            }

            // Service check name string, shouldn’t contain any |
            private static string EscapeName(string name)
            {
                name = EscapeContent(name);

                if (name.Contains("|"))
                {
                    throw new ArgumentException("Name must not contain any | (pipe) characters", "name");
                }

                return name;
            }

            private static string EscapeMessage(string message)
            {
                if (!string.IsNullOrEmpty(message))
                {
                    return EscapeContent(message).Replace("m:", "m\\:");
                }

                return message;
            }
        }
    }
}