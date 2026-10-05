using System.Net;
using System.Text;

namespace Campfire.Web;

public static class Translations
{
    public static string Button(string key)
    {
        if (!Phrases.TryGetValue(key, out var phrases))
            return "";
        var builder = new StringBuilder();
        builder.Append("<details class=\"position-relative\" data-controller=\"popup\" data-action=\"keydown.esc->popup#close toggle->popup#toggle click@document->popup#closeOnClickOutside\" data-popup-orientation-top-class=\"popup-orientation-top\">");
        builder.Append("<summary class=\"btn\" tabindex=\"-1\"><img src=\"/assets/images/globe.svg\" width=\"20\" height=\"20\" aria-hidden=\"true\" class=\"color-icon\"><span class=\"for-screen-reader\">Translate</span></summary>");
        builder.Append("<div class=\"language-list-menu shadow\" data-popup-target=\"menu\"><dl class=\"language-list\">");
        foreach (var (language, phrase) in phrases)
        {
            builder.Append("<dt>").Append(language).Append("</dt>");
            builder.Append("<dd class=\"margin-none\">").Append(WebUtility.HtmlEncode(phrase)).Append("</dd>");
        }

        builder.Append("</dl></div></details>");
        return builder.ToString();
    }

    private static readonly Dictionary<string, (string Language, string Phrase)[]> Phrases = new()
    {
        ["user_name"] = Row("Enter your name", "Introduce tu nombre", "Entrez votre nom", "अपना नाम दर्ज करें", "Geben Sie Ihren Namen ein", "Insira seu nome", "お名前を入力してください"),
        ["email_address"] = Row("Enter your email address", "Introduce tu correo electrónico", "Entrez votre adresse courriel", "अपना ईमेल पता दर्ज करें", "Geben Sie Ihre E-Mail-Adresse ein", "Insira seu endereço de email", "メールアドレスを入力してください"),
        ["password"] = Row("Enter your password", "Introduce tu contraseña", "Saisissez votre mot de passe", "अपना पासवर्ड दर्ज करें", "Geben Sie Ihr Passwort ein", "Insira sua senha", "パスワードを入力してください"),
        ["update_password"] = Row("Change password", "Cambiar contraseña", "Changer le mot de passe", "पासवर्ड बदलें", "Passwort ändern", "Alterar senha", "パスワードを変更"),
        ["bio"] = Row("Enter a few words about yourself.", "Ingresa algunas palabras sobre ti mismo.", "Saisissez quelques mots à propos de vous-même.", "अपने बारे में कुछ शब्द लिखें.", "Geben Sie ein paar Worte über sich selbst ein.", "Insira alguma palavras sobre você.", "ご自分について簡単に記入してください。"),
        ["invite_message"] = Row(
            "Welcome to Campfire. To invite some people to chat with you, share the join link below.",
            "Bienvenido a Campfire. Para invitar a algunas personas a chatear contigo, comparte el enlace de unión que se encuentra a continuación.",
            "Bienvenue sur Campfire. Pour inviter des personnes à discuter avec vous, partagez le lien pour rejoindre ci-dessous.",
            "Campfire में आपका स्वागत है। अधिक लोगों को चैट के लिए आमंत्रित करने के लिए, नीचे जुड़ने का लिंक साझा करें।",
            "Willkommen bei Campfire. Um einige Personen zum Chatten einzuladen, teilen Sie den unten stehenden Beitrittslink.",
            "Boas vindas ao Campfire. Para convidar pessoas para conversarem com você, compartilhe o link de convite abaixo.",
            "Campfireへようこそ。他の人をチャットに招待するには、下記の参加リンクを共有してください。"),
        ["incompatible_browser_messsage"] = Row(
            "Upgrade to a supported web browser. Campfire requires a modern web browser. Please use one of the browsers listed below and make sure auto-updates are enabled.",
            "Actualiza a un navegador web compatible. Campfire requiere un navegador web moderno. Utiliza uno de los navegadores listados a continuación y asegúrate de que las actualizaciones automáticas estén habilitadas.",
            "Mettez à jour vers un navigateur web pris en charge. Campfire nécessite un navigateur web moderne. Veuillez utiliser l'un des navigateurs répertoriés ci-dessous et assurez-vous que les mises à jour automatiques sont activées.",
            "समर्थित वेब ब्राउज़र में अपग्रेड करें। Campfire को एक आधुनिक वेब ब्राउज़र की आवश्यकता है। कृपया नीचे सूचीबद्ध ब्राउज़रों में से कोई एक का उपयोग करें और सुनिश्चित करें कि स्वचालित अपडेट्स सक्षम हैं।",
            "Aktualisieren Sie auf einen unterstützten Webbrowser. Campfire erfordert einen modernen Webbrowser. Verwenden Sie bitte einen der unten aufgeführten Browser und stellen Sie sicher, dass automatische Updates aktiviert sind.",
            "Atualize para um navegador compatível. O Campfire requer um navegador moderno. Por favor, use um dos navegadores listados abaixo e certifique-se de que as atualizações automáticas estão ativadas.",
            "サポートされたウェブブラウザーにアップグレードしてください。Campfireはモダンなウェブブラウザーが必要です。下記のブラウザーのいずれかを使用し、自動更新が有効になっていることを確認してください。"),
    };

    private static (string Language, string Phrase)[] Row(string en, string es, string fr, string hi, string de, string pt, string ja) =>
    [
        ("🇺🇸", en),
        ("🇪🇸", es),
        ("🇫🇷", fr),
        ("🇮🇳", hi),
        ("🇩🇪", de),
        ("🇧🇷", pt),
        ("🇯🇵", ja),
    ];
}
