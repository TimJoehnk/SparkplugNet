// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SparkplugNodeBase.cs" company="Hämmer Electronics">
// The project is licensed under the MIT license.
// </copyright>
// <summary>
//   A class that handles a Sparkplug node.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace SparkplugNet.Core.Node;

using System.ComponentModel.DataAnnotations;

/// <inheritdoc cref="SparkplugBase{T}"/>
/// <summary>
/// A class that handles a Sparkplug node.
/// </summary>
/// <seealso cref="SparkplugBase{T}"/>
public abstract partial class SparkplugNodeBase<T> : SparkplugBase<T> where T : IMetric, new()
{
    /// <summary>
    /// The options.
    /// </summary>
    protected SparkplugNodeOptions? Options { get; private set; }

    /// <inheritdoc cref="SparkplugBase{T}"/>
    /// <summary>
    /// Initializes a new instance of the <see cref="SparkplugNodeBase{T}"/> class.
    /// </summary>
    /// <param name="knownMetrics">The metric names.</param>
    /// <param name="logger">The logger.</param>
    /// <seealso cref="SparkplugBase{T}"/>
    public SparkplugNodeBase(IEnumerable<T> knownMetrics, ILogger? logger = null) : base(knownMetrics, logger)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SparkplugNodeBase{T}"/> class.
    /// </summary>
    /// <param name="knownMetricsStorage">The known metrics storage.</param>
    /// <param name="logger">The logger.</param>
    public SparkplugNodeBase(KnownMetricStorage knownMetricsStorage, ILogger? logger = null) : base(knownMetricsStorage, logger)
    {
    }

    /// <summary>
    /// Starts the Sparkplug node.
    /// </summary>
    /// <param name="nodeOptions">The node options.</param>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    public async Task Start(SparkplugNodeOptions nodeOptions)
    {
        if (this.IsRunning)
        {
            throw new InvalidOperationException("Start should only be called once!");
        }
        this.IsRunning = true;

        // Storing the options.
        this.Options = nodeOptions;

        if (this.Options is null)
        {
            throw new ArgumentNullException(nameof(this.Options), "The options aren't set properly.");
        }

        // Add handlers.
        this.AddEventHandler();
        this.AddMessageReceivedHandler();

        // Connect, subscribe to incoming messages and send a state message.
        await this.ConnectInternal();
        await this.SubscribeInternal();
        await this.PublishInternal();
    }

    /// <summary>
    /// Stops the Sparkplug node.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    public async Task Stop()
    {
        this.IsRunning = false;
        await this.Client.DisconnectAsync();
    }

    /// <summary>
    /// Publishes some metrics.
    /// </summary>
    /// <param name="metrics">The metrics.</param>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="Exception">The MQTT client is not connected or an invalid metric type was specified.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The namespace is out of range.</exception>
    /// <returns>A <see cref="MqttClientPublishResult"/>.</returns>
    public async Task<MqttClientPublishResult> PublishMetrics(IEnumerable<T> metrics)
    {
        if (this.Options is null)
        {
            throw new ArgumentNullException(nameof(this.Options), "The options aren't set properly.");
        }

        if (!this.Client.IsConnected)
        {
            throw new Exception("The MQTT client is not connected, please try again.");
        }

        return await this.PublishMessage(metrics);
    }

    /// <summary>
    /// Publishes metrics for a node.
    /// </summary>
    /// <param name="metrics">The metrics.</param>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <exception cref="Exception">An invalid metric type was specified.</exception>
    /// <returns>A <see cref="MqttClientPublishResult"/>.</returns>
    protected abstract Task<MqttClientPublishResult> PublishMessage(IEnumerable<T> metrics);

    /// <summary>
    /// Adds the disconnected handler and the reconnect functionality to the client.
    /// </summary>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    private void AddEventHandler()
    {
        this.Client.DisconnectedAsync += this.OnClientDisconnected;
        this.Client.ConnectedAsync += this.OnClientConnectedAsync;
    }

    /// <summary>
    /// Handles the client disconnection event.
    /// </summary>
    /// <param name="arg">The <see cref="MqttClientConnectedEventArgs"/> instance containing the event data.</param>
    protected virtual async Task OnClientConnectedAsync(MqttClientConnectedEventArgs arg)
    {
        this.Logger?.Information("Connection established");

        try
        {
            await this.FireConnectedAsync();
        }
        catch (Exception ex)
        {
            this.Logger?.Error(ex, "OnClientConnectedAsync");
            await Task.FromException(ex);
        }
    }
    /// <summary>
    /// Handles the client disconnection.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    private async Task OnClientDisconnected(MqttClientDisconnectedEventArgs args)
    {
        try
        {
            if (this.Options is null)
            {
                throw new ArgumentNullException(nameof(this.Options));
            }

            // Invoke disconnected callback.
            await this.FireDisconnectedAsync();

            this.Logger?.Warning("Connection lost, retrying to connect in {@reconnectInterval}", this.Options.ReconnectInterval);
            // Wait until the disconnect interval is reached.
            await Task.Delay(this.Options.ReconnectInterval);
            if (this.IsRunning)
            {
                // Connect, subscribe to incoming messages and send a state message.
                await this.ConnectInternal();
                await this.SubscribeInternal();
                await this.PublishInternal();
            }
        }
        catch (Exception ex)
        {
            this.Logger?.Error(ex, "OnClientDisconnectedAsync");
            await Task.FromException(ex);
        }
    }

    /// <summary>
    /// Adds the message received handler to handle incoming messages.
    /// </summary>
    /// <exception cref="InvalidCastException">The metric cast is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The namespace is out of range.</exception>
    private void AddMessageReceivedHandler()
    {
        this.Client.ApplicationMessageReceivedAsync += this.OnApplicationMessageReceived;
    }

    /// <summary>
    /// Handles the message received handler.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The namespace is out of range.</exception>
    private async Task OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;

        // Handle the STATE message before anything else as they're UTF-8 encoded.
        if (topic.Contains(SparkplugMessageType.StateMessage.GetDescription()))
        {
            await this.FireStatusMessageReceivedAsync(Encoding.UTF8.GetString(args.ApplicationMessage.Payload));
        }

        await this.OnMessageReceived(topic, args.ApplicationMessage.Payload);
    }

    /// <summary>
    /// Called when [message received].
    /// </summary>
    /// <param name="topic">The topic.</param>
    /// <param name="payload">The payload.</param>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    protected abstract Task OnMessageReceived(string topic, byte[] payload);

    /// <summary>
    /// Connects the Sparkplug node to the MQTT broker.
    /// </summary>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    private async Task ConnectInternal()
    {
        if (this.Options is null)
        {
            throw new ArgumentNullException(nameof(this.Options));
        }

        // Increment the session number.
        this.IncrementLastSessionNumber();

        // Reset the sequence number.
        this.ResetLastSequenceNumber();

        // Get the will message.
        var willMessage = this.MessageGenerator.GetSparkPlugNodeDeathMessage(
            this.NameSpace,
            this.Options.GroupIdentifier,
            this.Options.EdgeNodeIdentifier,
            this.LastSessionNumber);

        // Build up the MQTT client and connect.
        this.Options.CancellationToken ??= CancellationToken.None;

        var builder = new MqttClientOptionsBuilder()
            .WithClientId(this.Options.ClientId)
            .WithCredentials(this.Options.UserName, this.Options.Password)
            .WithCleanSession(false)
            .WithProtocolVersion(MqttProtocolVersion.V311);

        if (this.Options.UseTls)
        {
            if (this.Options.TlsParameters != null)
            {
                builder.WithTls(this.Options.TlsParameters);
            }
            else
            {
                builder.WithTls();
            }
        }

        if (this.Options.WebSocketParameters is null)
        {
            builder.WithTcpServer(this.Options.BrokerAddress, this.Options.Port);
        }
        else
        {
            builder.WithWebSocketServer(this.Options.BrokerAddress, this.Options.WebSocketParameters);
        }

        if (this.Options.ProxyOptions != null)
        {
            builder.WithProxy(
                this.Options.ProxyOptions.Address,
                this.Options.ProxyOptions.Username,
                this.Options.ProxyOptions.Password,
                this.Options.ProxyOptions.Domain,
                this.Options.ProxyOptions.BypassOnLocal);
        }
        // Add the will message data.
        builder.WithWillContentType(willMessage.ContentType);
        builder.WithWillCorrelationData(willMessage.CorrelationData);
        builder.WithWillDelayInterval(willMessage.MessageExpiryInterval);
        builder.WithWillPayload(willMessage.Payload);
        builder.WithWillPayloadFormatIndicator(willMessage.PayloadFormatIndicator);
        builder.WithWillQualityOfServiceLevel(willMessage.QualityOfServiceLevel);
        builder.WithWillResponseTopic(willMessage.ResponseTopic);
        builder.WithWillRetain(willMessage.Retain);
        builder.WithWillTopic(willMessage.Topic);

        if (willMessage.UserProperties != null)
        {
            foreach (var userProperty in willMessage.UserProperties)
            {
                builder.WithWillUserProperty(userProperty.Name, userProperty.Value);
            }
        }

        this.ClientOptions = builder.Build();

        // Debug output.
        this.Logger?.Debug("CONNECT Message: {@ClientOptions}", this.ClientOptions);

        await this.Client.ConnectAsync(this.ClientOptions, this.Options.CancellationToken.Value);
    }

    /// <summary>
    /// Publishes data to the MQTT broker.
    /// </summary>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    private async Task PublishInternal()
    {
        if (this.Options is null)
        {
            throw new ArgumentNullException(nameof(this.Options));
        }
        
        // Get the online message.
        var onlineMessage = this.MessageGenerator.GetSparkPlugNodeBirthMessage<T>(
            this.NameSpace,
            this.Options.GroupIdentifier,
            this.Options.EdgeNodeIdentifier,
            this.KnownMetrics,
            this.LastSequenceNumber,
            this.LastSessionNumber,
            DateTimeOffset.Now);

        // Publish data.
        this.Options.CancellationToken ??= CancellationToken.None;

        // Debug output.
        this.Logger?.Debug("NBIRTH Message: {@OnlineMessage}", onlineMessage);

        // Increment the sequence number.
        this.IncrementLastSequenceNumber();

        // Publish the message.
        await this.Client.PublishAsync(onlineMessage, this.Options.CancellationToken.Value);

        if (this.Options.PublishKnownDeviceMetricsOnReconnect)
        {
            foreach (var device in this.KnownDevices)
            {
                await this.PublishDeviceBirthMessage(device.Value, device.Key);
            }
        }
    }

    /// <summary>
    /// Subscribes the client to the node subscribe topics.
    /// </summary>
    /// <exception cref="ArgumentNullException">The options are null.</exception>
    /// <returns>A <see cref="Task"/> representing any asynchronous operation.</returns>
    private async Task SubscribeInternal()
    {
        if (this.Options is null)
        {
            throw new ArgumentNullException(nameof(this.Options));
        }

        var nodeCommandSubscribeTopic = SparkplugTopicGenerator.GetNodeCommandSubscribeTopic(this.NameSpace, this.Options.GroupIdentifier, this.Options.EdgeNodeIdentifier);
        await this.Client.SubscribeAsync(nodeCommandSubscribeTopic, (MqttQualityOfServiceLevel)SparkplugQualityOfServiceLevel.AtLeastOnce);

        var deviceCommandSubscribeTopic = SparkplugTopicGenerator.GetWildcardDeviceCommandSubscribeTopic(this.NameSpace, this.Options.GroupIdentifier, this.Options.EdgeNodeIdentifier);
        await this.Client.SubscribeAsync(deviceCommandSubscribeTopic, (MqttQualityOfServiceLevel)SparkplugQualityOfServiceLevel.AtLeastOnce);

        var stateSubscribeTopic = SparkplugTopicGenerator.GetStateSubscribeTopic(this.Options.ScadaHostIdentifier);
        await this.Client.SubscribeAsync(stateSubscribeTopic, (MqttQualityOfServiceLevel)SparkplugQualityOfServiceLevel.AtLeastOnce);
    }
}
