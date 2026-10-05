namespace Campfire.Web;

public static class UnsupportedBrowserPage
{
    public static readonly string Html = Template.Replace("TRANSLATION_BUTTON", Translations.Button("incompatible_browser_messsage"), StringComparison.Ordinal);

    private const string Template = """
        <!DOCTYPE html>
        <html>
        <head>
          <title>Unsupported browser</title>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1, user-scalable=no, interactive-widget=resizes-content">
          <meta name="color-scheme" content="light dark">
          <meta name="theme-color" content="#ffffff" media="(prefers-color-scheme: light)">
          <meta name="theme-color" content="#000000" media="(prefers-color-scheme: dark)">
          <link rel="stylesheet" href="/assets/application.css">
        </head>
        <body>
          <a href="#main-content" class="skip-navigation btn">Skip to main content</a>
          <nav id="nav"></nav>
          <div class="flash" hidden></div>
          <main id="main-content">
            <div class="panel center">
              <header>
                <h1 class="txt-x-large txt-tight-lines txt-align-center margin-none-block-start margin-block-end">Upgrade to a supported web browser</h1>
                <div class="flex align-start gap">
                  TRANSLATION_BUTTON
                  <p class="margin-none-block-start">Campfire requires a modern web browser. Please use one of the browsers listed below and make sure auto-updates are enabled.</p>
                </div>
              </header>
              <div class="browser-list flex align-center flex-wrap gap justify-center margin-block">
                <div class="browser flex flex-column">
                  <img src="/assets/images/browsers/safari.svg" alt="" class="center">
                  <div class="flex flex-column align-center margin-block-start-half"><strong>Safari</strong><span>17.2+</span></div>
                </div>
                <div class="browser flex flex-column">
                  <img src="/assets/images/browsers/chrome.svg" alt="" class="center">
                  <div class="flex flex-column align-center margin-block-start-half"><strong>Chrome</strong><span>120+</span></div>
                </div>
                <div class="browser flex flex-column">
                  <img src="/assets/images/browsers/firefox.svg" alt="" class="center">
                  <div class="flex flex-column align-center margin-block-start-half"><strong>Firefox</strong><span>121+</span></div>
                </div>
                <div class="browser flex flex-column">
                  <img src="/assets/images/browsers/opera.svg" alt="" class="center">
                  <div class="flex flex-column align-center margin-block-start-half"><strong>Opera</strong><span>104+</span></div>
                </div>
              </div>
            </div>
            <footer id="footer"></footer>
          </main>
          <aside id="sidebar"></aside>
          <dialog class="lightbox" aria-label="Image Viewer (Press escape to close)">
            <img src="" class="lightbox__image" alt="">
            <form method="dialog" class="lightbox__btn">
              <button class="btn" type="submit">
                <img src="/assets/images/remove.svg" alt="" width="20" height="20">
                <span class="for-screen-reader">Close image viewer</span>
              </button>
            </form>
            <a href="" class="lightbox__btn--download btn">
              <img src="/assets/images/download.svg" alt="" width="20" height="20">
              <span class="for-screen-reader">Download file</span>
            </a>
            <button class="lightbox__btn--share btn" type="button">
              <img src="/assets/images/share.svg" alt="" width="20" height="20">
              <span class="for-screen-reader">Share file</span>
            </button>
          </dialog>
          <a href="https://once.com" id="app-logo" target="_blank" aria-label="Once software from 37signals home page">
            <img src="/assets/images/campfire-icon.png" alt="Campfire logo" width="256" height="216">
          </a>
        </body>
        </html>
        """;
}
