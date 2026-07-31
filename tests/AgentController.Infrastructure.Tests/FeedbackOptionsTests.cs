using AgentController.Infrastructure.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentController.Infrastructure.Tests;

public class FeedbackOptionsTests
{
    [Fact]
    public void Defaults_PreserveRevivalAndConfigureAssistanceLabels()
    {
        var options = new FeedbackOptions();

        Assert.Equal("agent-rework-requested", options.ReworkMarkerTag);
        Assert.Equal("agent-assistance-requested", options.AssistanceMarkerTag);
        Assert.Equal("agent-assistance-in-progress", options.AssistanceInProgressTag);
    }

    [Fact]
    public void GlobalReviewerAllowlist_IsNotPartOfFeedbackOptions()
    {
        Assert.Null(typeof(FeedbackOptions).GetProperty("AllowedReviewers"));
    }

    [Fact]
    public void Labels_BindFromConfiguration()
    {
        var options = BindAndValidate(
            new Dictionary<string, string?>
            {
                ["feedback:reworkMarkerTag"] = "custom-rework-requested",
                ["feedback:assistanceMarkerTag"] = "custom-assistance-requested",
                ["feedback:assistanceInProgressTag"] = "custom-assistance-in-progress",
            }
        );

        Assert.Equal("custom-rework-requested", options.ReworkMarkerTag);
        Assert.Equal("custom-assistance-requested", options.AssistanceMarkerTag);
        Assert.Equal("custom-assistance-in-progress", options.AssistanceInProgressTag);
    }

    [Theory]
    [InlineData("feedback:assistanceMarkerTag", "")]
    [InlineData("feedback:assistanceInProgressTag", "   ")]
    public void AssistanceLabels_RejectEmptyValues(string key, string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            BindAndValidate(new Dictionary<string, string?> { [key] = value })
        );

        Assert.Contains(
            key[(key.LastIndexOf(':') + 1)..],
            exception.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Theory]
    [InlineData("feedback:assistanceMarkerTag")]
    [InlineData("feedback:assistanceInProgressTag")]
    public void AssistanceLabels_RejectRevivalLabelCaseInsensitively(string key)
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            BindAndValidate(
                new Dictionary<string, string?>
                {
                    ["feedback:reworkMarkerTag"] = "custom-rework-requested",
                    [key] = "CUSTOM-REWORK-REQUESTED",
                }
            )
        );

        Assert.Contains(nameof(FeedbackOptions.ReworkMarkerTag), exception.Message);
    }

    [Fact]
    public void AssistanceLabels_MustBeDistinctFromEachOther()
    {
        var exception = Assert.Throws<OptionsValidationException>(() =>
            BindAndValidate(
                new Dictionary<string, string?>
                {
                    ["feedback:assistanceMarkerTag"] = "custom-assistance",
                    ["feedback:assistanceInProgressTag"] = "CUSTOM-ASSISTANCE",
                }
            )
        );

        Assert.Contains(nameof(FeedbackOptions.AssistanceMarkerTag), exception.Message);
        Assert.Contains(nameof(FeedbackOptions.AssistanceInProgressTag), exception.Message);
    }

    private static FeedbackOptions BindAndValidate(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services
            .AddOptions<FeedbackOptions>()
            .Bind(configuration.GetSection(FeedbackOptions.SectionName))
            .ValidateDataAnnotations();

        return services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<FeedbackOptions>>()
            .Value;
    }
}
