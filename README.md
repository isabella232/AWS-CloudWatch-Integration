# Integrating the Apprenda Cloud Platform with CloudWatch
This is the repository that includes all the details to integrate Apprenda logging with the CloudWatch platform. With Apprenda version 6.6, we enabled the capability to forward all logs that the Apprenda Platform collects to a WCF service which can then forward them to any logging subsystem. All logs includes the logs from guest applications as well as the logs from the core platform services.

This repository will walk you through the steps to set up this integration as well as provide the sample code to get you started.

## Code Repository
- CloudWatch Add-On, an Apprenda Add-On that allows an operator to define the details of a provisioned CloudWatch account so that developers can use it inside a guest application
- LogForwarderExtension WCF Service, An Apprenda WCF service that is built as an extension. It receives all the logs from Apprenda and forwards them to CloudWatch
- TestingLoggingExtension_WCF_Call.ps1, a PowerShell utility to invoke the Test Interface of the LogAggregator and get some useful statistics on the queue that is pushing the log messages to CloudWatch

## Integration Steps, Setting up CloudWatch
- First, go ahead and create a CloudWatch account
- CloudWatch will provide you with a Key, a Secret, and the AWS Region where your logs will be forwarded. You will need those values when you configure the Add-On.
- You can learn more about CloudWatch at https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/GettingStarted.html

## Integration Steps, Setting up the Apprenda Add-On in the Apprenda Operator Portal
- Use the provided Apprenda.CloudWatch.AddOn.zip to upload the Add-On to the Apprenda SOC (aka Operator Portal). You can alternatively build or enhance the provided Visual Studio solution file to create an Add-On that meets your needs.
- Once the Add-On is uploaded in Apprenda, edit it
- Enter the Username and Password for the Add-On. These are your AWS Access Key and your AWS Secret Access Key respectively
- Now visit the "Configuration" tab
- In the `AWSRegion` field, enter the CloudWatch region (i.e us-east-1)
- Save the Add-On
- You can learn more about Add-Ons at http://docs.apprenda.com/8-1/addons

## Integration Steps, Setting up the Apprenda Add-On in the Apprenda Developer Portal
- Now visit the Apprenda Developer Portal and click on "Add-Ons"
- Find the Add-On you just created for CloudWatch, click on Manage and provision a new instance of it
- The sample code expects the new instance to be called `ForwardingLogs`. The name is important as it will generate an Apprenda Token that we will use in our code. The name of the token is `$#ADDON-ForwardingLogs#$` and you will find it is used inside the `CloudWatch.config` file of the LogAggregator project. When you deploy the application, Apprenda will token switch and replace the contents of the token with the real value of the connection to CloudWatch as provided by the Add-On
- You can learn more about token switching at http://docs.apprenda.com/8-1/app-config-tokens

## Integration Steps, Setting up the Apprenda WCF Service that will receive the logs from the Platform
- Upload the LogAggregator.zip Apprenda application archive as a new Application in the Apprenda Developer Portal. You can use any alias for your application, for example `logforwarder`
- You can also look at the included Visual Studio solution and make any modifications to the code as desired.
- You can learn more about Apprenda Extensions and how to take advantage of the `Apprenda.SaaSGrid.Extensions.LogAggregatorExtensionServiceBase` at http://docs.apprenda.com/8-1/extensions.
- `OnLogsPersisted` is the WCF endpoint called by Apprenda in frequent intervals and is passed an array of log messages from the Platform. All logs for all applications will be pushed to this endpoint, include core platform components and guest applications. The endpoint takes an array of Apprenda.SaasGrid.Extension.DTO.LogMessageDTO
- Once you have the logs, you can push them to CloudWatch or any other logging subsystem
- Be aware of a couple of performance and security recommendations on this WCF service
  - It is recommended that automatic scaling is configured for the WCF service receiving logs from the Platform. Although there is no performance penalty when you subscribe to the OnLogsPersisted hook point, using automatic scaling will allow the component to scale with the dynamic demand of logging on the Platform
  - It is recommended that WCF services receiving logs from the Platform should use the Authorized User Access Model. This access level will make sure that only authorized and authenticated entities can push logs to the WCF service
  - If you have any more questions about hardening your WCF service contact your Apprenda support representative
- Once you have pushed your service to Sandbox or Published stage level, visit the Apprenda Operator Portal for the last configuration piece. As per the instructions in the Apprenda Extensions page, you need to instruct Apprenda which WCF endpoint should receive the logs
- Visit the Platform Registry and create a new Registry Setting called `Telemetry.Logging.ForwardingServiceExtensionVersions`. In the value field, enter the name and version of the application you just created. For example: `logforwarder(v1)/LogService`. It is important to remember that if you version your application to v2, you need to come back and update this Registry setting.
  - Be aware that searching for this Registry setting in Apprenda may not yield any results. This is a hidden setting once created
- Your WCF service logforwarder will now start getting logs from the Apprenda Platform

## Integration Steps, Viewing the Logs in CloudWatch
- Your logs will automatically start getting pushed from Apprenda into CloudWatch
- Now, visit your CloudWatch instance, select the appropriate region that was configured in the Add-On and view your logs from Apprenda.

**Congratulations, you have just integrated the Apprenda Cloud Platform with AWS CloudWatch**
