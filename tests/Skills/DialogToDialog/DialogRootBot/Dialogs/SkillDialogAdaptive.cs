﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Integration.AspNet.Core.Skills;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.BotBuilderSamples.DialogRootBot.Dialogs
{
    /// <summary>
    /// EXPERIMENTAL: A sample dialog that can wrap remote calls to a skill using Adaptive.
    /// </summary>
    public class SkillDialogAdaptive : Dialog
    {
        private readonly string _botId;
        private readonly ConversationState _conversationState;
        private readonly SkillHttpClient _skillClient;
        private readonly SkillsConfiguration _skillsConfig;

        public SkillDialogAdaptive(ConversationState conversationState, SkillHttpClient skillClient, SkillsConfiguration skillsConfig, IConfiguration configuration, [CallerFilePath] string callerPath = "", [CallerLineNumber] int callerLine = 0)
            : base(nameof(SkillDialog))
        {
            _botId = configuration.GetSection(MicrosoftAppCredentials.MicrosoftAppIdKey)?.Value;
            _skillClient = skillClient;
            _skillsConfig = skillsConfig;
            _conversationState = conversationState ?? throw new ArgumentNullException(nameof(conversationState));
            RegisterSourceLocation(callerPath, callerLine);
        }

        /// <summary>
        /// Gets or sets SkillId to call.
        /// </summary>
        /// <value>
        /// SkillId to call.
        /// </value>
        [JsonProperty("method")]
        public string SkillId { get; set; }

        /// <summary>
        /// Gets or sets the event name you want to use to invoke the skill.
        /// </summary>
        /// <value>If this is null then default is to route the current activity to the skill. If this is set, then a EventActivity will be sent to the skill to initiate the conversation.</value>
        [JsonProperty("eventName")]
        public string EventName { get; set; }

        /// <summary>
        /// Gets or sets the value to pass to the skill.
        /// </summary>
        /// <value>
        /// The value to pass to the skill.
        /// </value>
        [JsonProperty("value")]
        public object Value { get; set; }

        /// <summary>
        /// Gets or sets the property path to store the result returned from the skill.
        /// </summary>
        /// <value>
        /// The property path to store the result returned from the skill.
        /// </value>
        public string ResultProperty { get; set; }

        public override async Task<DialogTurnResult> BeginDialogAsync(DialogContext dc, object options = null, CancellationToken cancellationToken = default)
        {
            if (options is CancellationToken)
            {
                throw new ArgumentException($"{nameof(options)} cannot be a cancellation token");
            }

            var dialogArgs = options as SkillDialogArgs;
            var skillId = dialogArgs?.SkillId ?? SkillId;
            dc.GetState().SetValue("this.SkillId", skillId);
            var eventName = dialogArgs?.Name ?? EventName;
            var boundValue = BindValue(dc, dialogArgs?.Value);
            var fwdActivity = dc.Context.Activity;

            if (!string.IsNullOrEmpty(eventName))
            {
                await dc.Context.SendActivityAsync($"SkillDialog: InBeginDialog using an event: {eventName}", cancellationToken: cancellationToken);
                var eventActivity = Activity.CreateEventActivity();
                eventActivity.Name = eventName;
                eventActivity.Value = boundValue;
                eventActivity.ApplyConversationReference(dc.Context.Activity.GetConversationReference());
                eventActivity.From = dc.Context.Activity.From;
                eventActivity.Recipient = dc.Context.Activity.Recipient;
                fwdActivity = (Activity)eventActivity;
            }
            else
            {
                await dc.Context.SendActivityAsync($"SkillDialog: InBeginDialog using pass through (activity is: {dc.Context.Activity.Type}).", cancellationToken: cancellationToken);
                fwdActivity.Value = boundValue;
            }

            // forward fwdActivity to the remote skill.
            return await SendToSkill(dc, fwdActivity, cancellationToken);
        }

        public override async Task<DialogTurnResult> ContinueDialogAsync(DialogContext dc, CancellationToken cancellationToken = default)
        {
            await dc.Context.SendActivityAsync($"SkillDialog: InContinueDialog, ActivityType: {dc.Context.Activity.Type}", cancellationToken: cancellationToken);

            if (dc.Context.Activity.Type == ActivityTypes.Message && dc.Context.Activity.Text.Equals("abort", StringComparison.CurrentCultureIgnoreCase))
            {
                // Send a message to the skill to let it do some cleanup
                var eocActivity = Activity.CreateEndOfConversationActivity();
                eocActivity.ApplyConversationReference(dc.Context.Activity.GetConversationReference());
                eocActivity.From = dc.Context.Activity.From;
                eocActivity.Recipient = dc.Context.Activity.Recipient;
                await SendToSkill(dc, (Activity)eocActivity, cancellationToken);

                // End this dialog and return (we don't care if the skill response or not)
                await dc.Context.SendActivityAsync("SkillDialog: Cancelled", cancellationToken: cancellationToken);
                return await dc.EndDialogAsync(cancellationToken: cancellationToken);
            }

            if (dc.Context.Activity.Type == ActivityTypes.EndOfConversation && (string)dc.Context.Activity.Recipient.Properties["SkillId"] == SkillId)
            {
                // look at the dc.Context.Activity.Code for exit status.
                await dc.Context.SendActivityAsync("SkillDialog: got EndOfConversation", cancellationToken: cancellationToken);
                if (ResultProperty != null)
                {
                    // set the result of the remote skill into ResultProperty memory
                    dc.GetState().SetValue(ResultProperty, dc.Context.Activity.Value);
                }

                return await dc.EndDialogAsync(dc.Context.Activity.Value, cancellationToken);
            }

            // Just forward to the remote skill
            return await SendToSkill(dc, dc.Context.Activity, cancellationToken);
        }

        //public override async Task EndDialogAsync(ITurnContext turnContext, DialogInstance instance, DialogReason reason, CancellationToken cancellationToken = default)
        //{
        //    await turnContext.SendActivityAsync("SkillDialog: In EndDialog", cancellationToken: cancellationToken);
        //    await base.EndDialogAsync(turnContext, instance, reason, cancellationToken);
        //}

        protected override string OnComputeId()
        {
            return $"{GetType().Name}[{SkillId}|{EventName}]";
        }

        protected object BindValue(DialogContext dc, object value)
        {
            // binding options are static definition of options with overlay of passed in options);
            var bindingValue = (JObject)ObjectPath.Merge(Value ?? new JObject(), value ?? new JObject());
            var boundValue = bindingValue;

            // TODO, when fully declarative we should support dynamic binding of values
            // This code relies on the ExpressionEngine to evaluate strings in the bindingValue object
            // var boundValue = new JObject();
            // foreach (var binding in bindingValue)
            // {
            //    // evalute the value
            //    var (result, error) = new ExpressionEngine().Parse(binding.Value.ToString()).TryEvaluate(dc.State);
            //    if (error != null)
            //    {
            //        throw new Exception(error);
            //    }
            //    // and store in options as the result
            //    boundValue[binding.Key] = JToken.FromObject(result);
            // }
            return boundValue;
        }

        private async Task<DialogTurnResult> SendToSkill(DialogContext dc, Activity activity, CancellationToken cancellationToken, bool saveState = true)
        {
            var skillId = dc.GetState().GetValue<string>("this.SkillId");

            // TODO: consider having an extension method in DC that saves state for you.
            // Always save state before forwarding
            // (the dialog stack won't get updated with the skillDialog and 'things won't work if you don't)
            await _conversationState.SaveChangesAsync(dc.Context, true, cancellationToken);
            var response = await _skillClient.PostActivityAsync(_botId, _skillsConfig.Skills[skillId], _skillsConfig.SkillHostEndpoint, activity, cancellationToken);
            if (response.Status != 200)
            {
                throw new HttpRequestException($"Error invoking the skill id: \"{skillId}\" at \"{_skillsConfig.Skills[skillId].SkillEndpoint}\" (status is {response.Status}). \r\n {response.Body}");
            }

            return EndOfTurn;
        }
    }
}
