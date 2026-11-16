using System.Diagnostics;
using PayloadPanda.Services.Scripting;

namespace PayloadPanda.Tests;

public class ScriptRunnerTests
{
    private static ScriptContext TestContext(string body = "{}", int code = 200, Dictionary<string, string>? variables = null) => new()
    {
        EventName = "test",
        RequestName = "Get token",
        Request = new ScriptRequest("POST", "https://login.example.com/oauth/token",
            [new("Content-Type", "application/json")], "{\"client_id\":\"abc\"}"),
        Response = new ScriptResponse(code, code == 200 ? "OK" : "Bad Request", 41.6, body.Length,
            [new("Content-Type", "application/json"), new("X-Request-Id", "r-1")], body),
        Variables = variables ?? new Dictionary<string, string> { ["machineToken"] = "", ["crn"] = "42" }
    };

    private static ScriptResult Run(string script, ScriptContext? context = null) =>
        ScriptRunner.Run(context ?? TestContext(), [script]);

    [Fact]
    public void A_test_script_stores_a_token_from_the_json_response()
    {
        var result = Run(
            """
            const body = pm.response.json();
            pm.collectionVariables.set('machineToken', body.access_token);
            console.log('scope granted:', body.scope);
            """,
            TestContext("{\"access_token\":\"tok-123\",\"scope\":\"create:locations\"}"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("tok-123", result.Variables["machineToken"]);
        Assert.Equal("42", result.Variables["crn"]);
        Assert.Equal(new ScriptConsoleLine("log", "scope granted: create:locations"), Assert.Single(result.Console));
    }

    [Fact]
    public void Tests_pass_and_fail_individually()
    {
        var result = Run(
            """
            const p = pm.response.json();
            const phoneOk = /^[a-zA-Z0-9\-()+ ]{1,15}$/.test(p.phoneNumber || '');
            pm.test('phone matches', () => pm.expect(phoneOk, 'phone length=' + (p.phoneNumber || '').length).to.be.true);
            pm.test('status is 200', () => pm.response.to.have.status(200));
            pm.test('name is a string', function () { pm.expect(p.firstName).to.be.a('string'); });
            pm.test.skip('not yet');
            (p.organisations || []).forEach(o => console.log('org', o.orgId, 'state=', o.address && o.address.state));
            """,
            TestContext("{\"phoneNumber\":\"+61 400 000 000 000\",\"firstName\":\"Ada\",\"organisations\":[{\"orgId\":\"o1\",\"address\":{\"state\":\"NSW\"}}]}"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(
            [("phone matches", false), ("status is 200", true), ("name is a string", true), ("not yet", true)],
            result.Tests.Select(t => (t.Name, t.Passed)));
        Assert.Equal("phone length=19: expected false to be true", result.Tests[0].Error);
        Assert.True(result.Tests[3].Skipped);
        Assert.Equal("org o1 state= NSW", result.Console[0].Text);
    }

    [Theory]
    [InlineData("pm.expect(1 + 1).to.equal(2)")]
    [InlineData("pm.expect({a: [1, {b: 2}]}).to.eql({a: [1, {b: 2}]})")]
    [InlineData("pm.expect({a: 1}).to.deep.equal({a: 1})")]
    [InlineData("pm.expect('hello world').to.include('lo w')")]
    [InlineData("pm.expect([1, 2, 3]).to.include(2).and.to.have.lengthOf(3)")]
    [InlineData("pm.expect({a: 1, b: 2}).to.include({b: 2})")]
    [InlineData("pm.expect({a: 1}).to.have.property('a', 1)")]
    [InlineData("pm.expect({a: 1}).to.not.have.property('z')")]
    [InlineData("pm.expect(5).to.be.above(4).and.below(6).and.at.least(5).and.at.most(5)")]
    [InlineData("pm.expect('abc-1').to.match(/^[a-z]+-\\d$/)")]
    [InlineData("pm.expect(null).to.be.null; pm.expect(undefined).to.be.undefined; pm.expect(0).to.not.be.ok")]
    [InlineData("pm.expect([]).to.be.an('array').that.is.empty")]
    [InlineData("pm.expect('b').to.be.oneOf(['a', 'b'])")]
    [InlineData("pm.expect(pm.response.code).to.equal(200); pm.response.to.be.ok")]
    [InlineData("pm.expect(pm.response.headers.get('x-request-id')).to.equal('r-1'); pm.response.to.have.header('Content-Type')")]
    [InlineData("pm.expect(pm.response.responseTime).to.equal(42)")]
    [InlineData("pm.expect(pm.request.method).to.equal('POST'); pm.expect('' + pm.request.url).to.include('login.example.com')")]
    [InlineData("pm.expect(pm.info.requestName).to.equal('Get token'); pm.expect(pm.info.eventName).to.equal('test')")]
    [InlineData("pm.expect(pm.variables.replaceIn('crn={{crn}} {{nope}}')).to.equal('crn=42 {{nope}}')")]
    [InlineData("pm.expect(atob(btoa('user:pässword'))).to.equal('user:pässword'); pm.expect(btoa('ab')).to.equal('YWI=')")]
    public void Supported_assertions_and_properties_hold(string script)
    {
        var result = Run(script);

        Assert.True(result.Succeeded, result.Error);
    }

    [Theory]
    [InlineData("pm.expect(1).to.equal(2)", "expected 1 to equal 2")]
    [InlineData("pm.expect('a').to.not.equal('a')", "expected a not to equal a")]
    [InlineData("pm.response.to.have.status(201)", "expected response to have status 201 but got 200 OK")]
    [InlineData("pm.sendRequest('https://example.com', () => {})", "pm.sendRequest is not supported in PayloadPanda")]
    [InlineData("const _ = require('lodash')", "require('lodash') is not supported in PayloadPanda")]
    [InlineData("postman.setNextRequest('x')", "postman.setNextRequest is not supported in PayloadPanda")]
    [InlineData("pm.cookies.get('a')", "pm.cookies is not supported in PayloadPanda")]
    [InlineData("pm.response.json()", "JSON")]
    [InlineData("nope.call()", "nope is not defined")]
    [InlineData("this is not javascript", "")]
    public void Failures_stop_the_script_with_a_message(string script, string expectedInError)
    {
        var result = Run(script, TestContext(body: "<html>"));

        Assert.False(result.Succeeded);
        Assert.Contains(expectedInError, result.Error);
    }

    [Fact]
    public void An_uncaught_error_reports_its_line_and_keeps_earlier_output()
    {
        var result = Run("pm.collectionVariables.set('a', 1);\nconsole.log('before');\nthrow new Error('boom');\nconsole.log('after');");

        Assert.Equal("boom (line 3)", result.Error);
        Assert.Equal("1", result.Variables["a"]);
        Assert.Equal(["before"], result.Console.Select(c => c.Text));
    }

    [Fact]
    public void A_pre_request_script_changes_headers_and_variables_and_sees_no_response()
    {
        var context = new ScriptContext
        {
            EventName = "prerequest",
            Request = new ScriptRequest("GET", "{{baseUrl}}/items", [new("Accept", "text/plain"), new("X-Old", "1")], string.Empty),
            Variables = new Dictionary<string, string> { ["crn"] = "42", ["gone"] = "x" }
        };

        var result = ScriptRunner.Run(context,
        [
            """
            pm.request.headers.upsert({ key: 'accept', value: 'application/json' });
            pm.request.headers.add({ key: 'X-Crn', value: pm.variables.get('crn') });
            pm.request.headers.remove('x-old');
            pm.collectionVariables.unset('gone');
            pm.collectionVariables.set('stamp', { n: 1 });
            pm.environment.set('fromEnv', true);
            pm.test('no response yet', () => pm.expect(pm.response).to.be.undefined);
            """
        ]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([new("accept", "application/json"), new("X-Crn", "42")], result.RequestHeaders);
        Assert.Equal(new Dictionary<string, string> { ["crn"] = "42", ["stamp"] = "{\"n\":1}", ["fromEnv"] = "true" }, result.Variables);
        Assert.True(Assert.Single(result.Tests).Passed);
        Assert.Contains(result.Console, c => c.Level == "warn" && c.Text.Contains("pm.environment is mapped"));
    }

    [Fact]
    public void Scripts_run_in_order_and_share_variables_but_not_globals()
    {
        var result = ScriptRunner.Run(TestContext(),
        [
            "var leaked = 1; pm.collectionVariables.set('step', 'collection');",
            "   ",
            "pm.test('isolated', () => pm.expect(typeof leaked).to.equal('undefined')); pm.collectionVariables.set('step', pm.collectionVariables.get('step') + '>request');"
        ]);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(Assert.Single(result.Tests).Passed);
        Assert.Equal("collection>request", result.Variables["step"]);
    }

    [Fact]
    public void A_failed_script_stops_the_chain()
    {
        var result = ScriptRunner.Run(TestContext(), ["throw new Error('first');", "pm.collectionVariables.set('ran', 'yes');"]);

        Assert.StartsWith("first", result.Error);
        Assert.False(result.Variables.ContainsKey("ran"));
    }

    [Fact]
    public void An_endless_loop_is_stopped_by_the_timeout()
    {
        var watch = Stopwatch.StartNew();

        var result = ScriptRunner.Run(TestContext(), ["console.log('start'); while (true) {}"], timeout: TimeSpan.FromMilliseconds(300));

        Assert.Contains("was stopped", result.Error);
        Assert.Equal("start", Assert.Single(result.Console).Text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Runaway_recursion_is_stopped()
    {
        var result = Run("function f() { return f(); } f();");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Cancellation_stops_a_running_script()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ScriptRunner.Run(TestContext(), ["while (true) {}"], cts.Token, TimeSpan.FromSeconds(30)));
    }

    [Theory]
    [InlineData("typeof System")]
    [InlineData("typeof importNamespace")]
    [InlineData("typeof process")]
    [InlineData("typeof fetch")]
    [InlineData("typeof XMLHttpRequest")]
    public void Nothing_from_dotnet_or_the_network_is_reachable(string expression)
    {
        var result = Run($"pm.collectionVariables.set('probe', {expression});");

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("undefined", result.Variables["probe"]);
    }
}
