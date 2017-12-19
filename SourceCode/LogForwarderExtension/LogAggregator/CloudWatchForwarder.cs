using System;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Apprenda.Services.Logging;
using System.Xml;
using System.IO;
using System.Threading;
using Apprenda.SaaSGrid.Extensions.DTO;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using System.Collections.Generic;

namespace LogAggregator
{
    class CloudWatchJSON
    {
        public string AWSAccessKey { get; set; }
        public string AWSSecretAccessKey { get; set; }
        public string AWSRegionEndpoint { get; set; }
    }

    internal class CloudWatchForwarder
    {
        private readonly string cloudWatchAccessKey;
        private readonly string cloudWatchSecretKey;
        private readonly RegionEndpoint cloudWatchRegion;
        private readonly string logGroupName;
        private Dictionary<string, string> LogStreamTokenMap = new Dictionary<string, string>();
        private Object criticalSection = new Object();

        internal CloudWatchForwarder()
        {            
            var cloudWatchEnvironmentVariableJSON = ReadConfigSettings("CloudWatchConnectionDetails");
            CloudWatchJSON cloudWatch = JsonConvert.DeserializeObject<CloudWatchJSON>(cloudWatchEnvironmentVariableJSON);
            this.cloudWatchAccessKey = cloudWatch.AWSAccessKey;
            this.cloudWatchSecretKey = cloudWatch.AWSSecretAccessKey;
            this.cloudWatchRegion = ParseRegionEndpoint(cloudWatch.AWSRegionEndpoint, true);
            this.logGroupName = "Apprenda-" + ReadConfigSettings("ApprendaCloudAlias");
        }

        internal void ForwardLogs()
        {
            do
            {
                Action pushLogsToCloudWatchAction = () =>
                {
                LogMessageDTO logMessageObj;
                    while (LogService.taskQueue.TryDequeue(out logMessageObj))
                    {
                        try
                        {
                            using (IAmazonCloudWatchLogs logsclient = Amazon.AWSClientFactory.CreateAmazonCloudWatchLogsClient(this.cloudWatchAccessKey, this.cloudWatchSecretKey, this.cloudWatchRegion))
                            {
                                string logStreamName = string.IsNullOrWhiteSpace(logMessageObj.ApplicationAlias) ? "Custom" : logMessageObj.ApplicationAlias;

                                // put the object into JSON format and send it to CloudWatch
                                List<InputLogEvent> logEvents = new List<InputLogEvent>();
                                InputLogEvent logEntry = new InputLogEvent();
                                logEntry.Message = JsonConvert.SerializeObject(logMessageObj);
                                logEntry.Timestamp = logMessageObj.Timestamp;
                                logEvents.Add(logEntry);
                                PutLogEventsRequest request = new PutLogEventsRequest(this.logGroupName, logStreamName, logEvents);

                                PutLogEventsResponse response = null;
                                for (int i = 0; i < 5; ++i)
                                {
                                    try
                                    {
                                        lock (this.criticalSection)
                                        {
                                            // if we have a token, set it
                                            if (LogStreamTokenMap.ContainsKey(logStreamName))
                                            {
                                                request.SequenceToken = LogStreamTokenMap[logStreamName];
                                            }

                                            // put the logs and get the token for the next submissions of logs
                                            response = logsclient.PutLogEvents(request);
                                            var newToken = response.NextSequenceToken;
                                            if (LogStreamTokenMap.ContainsKey(logStreamName))
                                            {
                                                LogStreamTokenMap[logStreamName] = newToken;
                                            }
                                            else
                                            {
                                                LogStreamTokenMap.Add(logStreamName, newToken);
                                            }
                                        }

                                        // if we successfully pushed the logs, exit the loop, otherwise we will exit in 5 tries
                                        break;
                                    }
                                    catch (InvalidSequenceTokenException)
                                    {
                                        // we don't have the right token for the next sequence in the stream, so get it again
                                        // in fact we will refresh all tokens for all streams
                                        var logstreamsrequest = new DescribeLogStreamsRequest(this.logGroupName);
                                        var logStreamResponse = logsclient.DescribeLogStreams(logstreamsrequest);
                                        var logstreamsList = logStreamResponse.LogStreams;

                                        lock (this.criticalSection)
                                        {
                                            foreach (var logstream in logstreamsList)
                                            {
                                                var appname = logstream.LogStreamName;
                                                var token = logstream.UploadSequenceToken;
                                                if (LogStreamTokenMap.ContainsKey(appname))
                                                {
                                                    LogStreamTokenMap[appname] = token;
                                                }
                                                else
                                                {
                                                    LogStreamTokenMap.Add(appname, token);
                                                }
                                            }
                                        }
                                    }
                                    catch (ResourceNotFoundException)
                                    {
                                        // we likely introduced a new log stream that needs to be provisioned in CloudWatch
                                        // ignore exceptions in creation
                                        try
                                        {
                                            CreateLogGroupRequest logGroup = new CreateLogGroupRequest(this.logGroupName);
                                            logsclient.CreateLogGroup(logGroup);
                                        }
                                        catch (Exception)
                                        { }

                                        try
                                        {
                                            CreateLogStreamRequest logStream = new CreateLogStreamRequest(this.logGroupName, logStreamName);
                                            logsclient.CreateLogStream(logStream);
                                            lock (this.criticalSection)
                                            {
                                                LogStreamTokenMap.Remove(logStreamName);
                                            }
                                        }
                                        catch (Exception)
                                        { }
                                    }
                                } 

                                if (response.HttpStatusCode == HttpStatusCode.OK)
                                {
                                    ++LogService.successfulForwards;
                                }
                                else
                                {
                                    ++LogService.failedForwards;
                                }
                            }
                        }
                        catch (Exception)
                        {
                            // can't really log this exception to avoid cyclical logging and deadlocks
                            ++LogService.numExceptions;
                        }
                    }
                };

                // Start 5 parallel tasks to consume the logs from the queue
                // the problem here is that the logs might not be pushed in order to CloudWatch 
                // However, that's ok since there might be multiple instances of this service and logs could be coming
                // in to all instances at the same time and we might push logs to CloudWatch out of order
                Parallel.Invoke(pushLogsToCloudWatchAction, pushLogsToCloudWatchAction, pushLogsToCloudWatchAction, pushLogsToCloudWatchAction, pushLogsToCloudWatchAction);

                // sleep before getting into another iteration of this loop to look for logs
                Thread.Sleep(1000);
            } while (true);                                       
        }

        private string ReadConfigSettings(string keyToRead)
        {
            string configDir = AppDomain.CurrentDomain.BaseDirectory;
            //var logger = LogManager.Instance().GetLogger(typeof(LogService));
            //logger.Log(configDir, LogLevel.Fatal);

            string[] configFiles = Directory.GetFiles(configDir, "CloudWatch.config", SearchOption.TopDirectoryOnly);
            if (configFiles.Length != 1)
            {
                throw new Exception("Failed to find the CloudWatch.config file at " + configDir);
            }

            XmlDocument xmlDoc = new XmlDocument();
            xmlDoc.Load(configFiles[0]);
            XmlNode appsettingsNode = xmlDoc.SelectSingleNode("//appSettings");
            if (null == appsettingsNode)
            {
                throw new Exception("Failed to find the appSettings node in CloudWatch.config file at " + configDir);
            }

            XmlElement connectionElement = (XmlElement)xmlDoc.SelectSingleNode(string.Format("//appSettings/add[@key = '{0}']", keyToRead));
            if (null == connectionElement)
            {
                throw new Exception("Failed to find the CloudWatch elements in node in CloudWatch.config file at " + configDir);
            }
            
            return connectionElement.Attributes["value"].Value;            
        }

        public static RegionEndpoint ParseRegionEndpoint(string _input, bool _defaultOnError = false)
        {
            // If defaultOnError is set to true, the default will always be USEast1, to be safe.
            if (_input.ToLowerInvariant().Equals("us-east-1") || _input.ToLowerInvariant().Contains("useast")) return RegionEndpoint.USEast1;
            if (_input.ToLowerInvariant().Equals("us-west-1") || _input.ToLowerInvariant().Contains("uswest")) return RegionEndpoint.USWest1;
            if (_input.ToLowerInvariant().Equals("us-west-2")) return RegionEndpoint.USWest2;
            if (_input.ToLowerInvariant().Equals("us-govcloudwest-1") || _input.ToLowerInvariant().Contains("usgov")) return RegionEndpoint.USGovCloudWest1;
            if (_input.ToLowerInvariant().Equals("sa-east-1") || _input.ToLowerInvariant().Contains("saeast")) return RegionEndpoint.SAEast1;
            if (_input.ToLowerInvariant().Equals("eu-central-1") || _input.ToLowerInvariant().Contains("eucentral")) return RegionEndpoint.EUCentral1;
            if (_input.ToLowerInvariant().Equals("eu-west-1") || _input.ToLowerInvariant().Contains("euwest")) return RegionEndpoint.EUWest1;
            if (_input.ToLowerInvariant().Equals("ap-northeast-1") || _input.ToLowerInvariant().Contains("apnortheast")) return RegionEndpoint.APNortheast1;
            if (_input.ToLowerInvariant().Equals("ap-southeast-1") || _input.ToLowerInvariant().Contains("apsoutheast")) return RegionEndpoint.APSoutheast1;
            if (_input.ToLowerInvariant().Equals("ap-southeast-2")) return RegionEndpoint.APSoutheast2;
            
            // if it fails and _defaultOnError is true, just return the default region.
            if (_defaultOnError)
            {
                return RegionEndpoint.USEast1;
            }
            throw new ArgumentException("Unable to determine AWS Region, could not parse {0}.", _input);
        }
    }
}