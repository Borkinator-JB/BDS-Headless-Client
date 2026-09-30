namespace Bds.Host;

static class LoginPage
{
    public static string Html(bool failed) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>BDS Headless</title><link rel="stylesheet" href="/app.css"></head>
        <body><main class="login">
        <h1>BDS Headless</h1>
        <form method="post" action="/login">
          <label>Password <input type="password" name="password" autofocus required></label>
          {{(failed ? "<p class=\"error\">Wrong password</p>" : "")}}
          <button type="submit">Sign in</button>
        </form></main></body></html>
        """;
}
