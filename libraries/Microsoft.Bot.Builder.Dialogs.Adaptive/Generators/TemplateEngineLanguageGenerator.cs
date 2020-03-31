﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Bot.Builder.Dialogs.Debugging;
using Microsoft.Bot.Builder.Dialogs.Declarative.Resources;
using Microsoft.Bot.Builder.LanguageGeneration;
using Newtonsoft.Json;

namespace Microsoft.Bot.Builder.Dialogs.Adaptive.Generators
{
    /// <summary>
    /// ILanguageGenerator implementation which uses LGFile. 
    /// </summary>
    public class TemplateEngineLanguageGenerator : LanguageGenerator
    {
        [JsonProperty("$kind")]
        public const string DeclarativeType = "Microsoft.TemplateEngineLanguageGenerator";

        private const string DEFAULTLABEL = "Unknown";

        private readonly LanguageGeneration.Templates lg;

        /// <summary>
        /// Initializes a new instance of the <see cref="TemplateEngineLanguageGenerator"/> class.
        /// </summary>
        public TemplateEngineLanguageGenerator()
        {
            this.lg = new LanguageGeneration.Templates();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TemplateEngineLanguageGenerator"/> class.
        /// </summary>
        /// <param name="engine">template engine.</param>
        public TemplateEngineLanguageGenerator(LanguageGeneration.Templates engine = null)
        {
            this.lg = engine ?? new LanguageGeneration.Templates();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TemplateEngineLanguageGenerator"/> class.
        /// </summary>
        /// <param name="lgText">lg template text.</param>
        /// <param name="id">optional label for the source of the templates (used for labeling source of template errors).</param>
        /// <param name="resourceMapping">template resource loader delegate (locale) -> <see cref="ImportResolverDelegate"/>.</param>
        public TemplateEngineLanguageGenerator(string lgText, string id, Dictionary<string, IList<IResource>> resourceMapping)
        {
            this.Id = id ?? DEFAULTLABEL;
            var (_, locale) = LGResourceLoader.ParseLGFileName(id);
            var importResolver = LanguageGeneratorManager.ResourceExplorerResolver(locale, resourceMapping);
            this.lg = LanguageGeneration.Templates.ParseText(lgText ?? string.Empty, Id, importResolver);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TemplateEngineLanguageGenerator"/> class.
        /// </summary>
        /// <param name="filePath">lg template file absolute path.</param>
        /// <param name="resourceMapping">template resource loader delegate (locale) -> <see cref="ImportResolverDelegate"/>.</param>
        public TemplateEngineLanguageGenerator(string filePath, Dictionary<string, IList<IResource>> resourceMapping)
        {
            filePath = PathUtils.NormalizePath(filePath);
            this.Id = Path.GetFileName(filePath);

            var (_, locale) = LGResourceLoader.ParseLGFileName(Id);
            var importResolver = LanguageGeneratorManager.ResourceExplorerResolver(locale, resourceMapping);
            this.lg = LanguageGeneration.Templates.ParseFile(filePath, importResolver);
            this.lg.RegisterSourceMap();
        }

        /// <summary>
        /// Gets or sets id of the source of this template (used for labeling errors).
        /// </summary>
        /// <value>
        /// Id of the source of this template (used for labeling errors).
        /// </value>
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Method to generate text from given template and data.
        /// </summary>
        /// <param name="turnContext">Context for the current turn of conversation.</param>
        /// <param name="template">template to evaluate.</param>
        /// <param name="data">data to bind to.</param>
        /// <returns>generated text.</returns>
        public override async Task<string> Generate(ITurnContext turnContext, string template, object data)
        {
            EventHandler onEvent = (object sender, EventArgs e) =>
            {
                if (e is BeginTemplateEvaluationArgs be && be.Source != "inline content")
                {
                    Console.WriteLine($"Running into template {be.TemplateName} in {be.Source}");
                    var task = turnContext.GetDebugger().StepAsync(new DialogContext(new DialogSet(), turnContext, new DialogState()), sender, be.Type, new System.Threading.CancellationToken());
                    task.Wait();
                }
                else if (e is BeginExpressionEvaluationArgs expr && expr.Source != "inline content")
                {
                    Console.WriteLine($"Running expression {expr.Expression} in {expr.Source}");
                    var task = turnContext.GetDebugger().StepAsync(new DialogContext(new DialogSet(), turnContext, new DialogState()), sender, expr.Type, new System.Threading.CancellationToken());
                    task.Wait();
                }
                else if (e is MessageArgs message && message.Source != "inline content")
                {
                    Console.WriteLine($"{message.Text}");
                    var dda = turnContext.GetDebugger() as IDebugger;
                    if (dda != null)
                    {
                        var task = dda.OutputAsync(message.Text, "message", message.Text, new System.Threading.CancellationToken());
                        task.Wait();
                    }
                }
            };

            try
            {
                var result = await Task.FromResult(lg.EvaluateText(template, data, onEvent).ToString());
                return result;
            }
            catch (Exception err)
            {
                if (!string.IsNullOrEmpty(this.Id))
                {
                    throw new Exception($"{Id}:{err.Message}");
                }

                throw;
            }
        }
    }
}
