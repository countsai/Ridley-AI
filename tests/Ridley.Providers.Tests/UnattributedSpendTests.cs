using System.Net;
using Ridley.Core;
using Ridley.Providers.OceanToken;

namespace Ridley.Providers.Tests;

public class UnattributedSpendTests
{
    [Fact]
    public async Task Spend_from_calls_that_report_no_cost_is_attributed_when_the_next_call_reveals_it_and_counts_toward_the_limit()
    {
        // call 1 reports spend 1.000 and cost 0.001; call 2 is a "stream" with no headers but really cost 0.010;
        // call 3 reports spend 1.011 and cost 0.001 => 0.010 appeared that nobody claimed.
        var responses = new Queue<HttpResponseMessage>(
        [
            FakeHandler.Json(HttpStatusCode.OK, "{}", ("x-oceantoken-cost", "0.001"), ("x-oceantoken-key-spend", "1.000")),
            FakeHandler.Json(HttpStatusCode.OK, "{}"),
            FakeHandler.Json(HttpStatusCode.OK, "{}", ("x-oceantoken-cost", "0.001"), ("x-oceantoken-key-spend", "1.011")),
        ]);
        var usage = new InMemoryUsageTracker();
        var client = new OceanTokenClient(new HttpClient(new FakeHandler((_, _, _) => Task.FromResult(responses.Dequeue()))),
            new FakeSecrets("sk-test"), usage, new OceanTokenOptions { SessionSpendLimitUsd = 0.0105m }, new ListLogger<OceanTokenClient>());
        var body = new System.Text.Json.Nodes.JsonObject();

        for (var i = 0; i < 3; i++)
        {
            var r = await client.PostJsonAsync("/x", "chat", "m", body, TimeSpan.FromSeconds(5), default);
            client.RecordSuccess("chat", "m", r);
        }

        Assert.Equal(0.010m, usage.Totals.UnattributedSpendUsd);
        Assert.Equal(0.012m, usage.Totals.EstimatedSpendUsd);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => client.PostJsonAsync("/x", "chat", "m", body, TimeSpan.FromSeconds(5), default));
        Assert.Equal(ErrorKind.Denied, ex.Kind); // known cost alone (0.002) would have stayed under the limit
    }
}
