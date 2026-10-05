(() => {
  if ("serviceWorker" in navigator)
    navigator.serviceWorker.register("/service-worker").catch(() => {});

  document.querySelectorAll("[data-copy]").forEach((button) => {
    button.addEventListener("click", () => navigator.clipboard.writeText(button.dataset.copy || ""));
  });
  document.querySelectorAll("[data-share]").forEach((button) => {
    button.addEventListener("click", async () => {
      const url = button.dataset.share || "";
      if (navigator.share)
        await navigator.share({ title: button.dataset.shareTitle || "Campfire", url });
      else
        await navigator.clipboard.writeText(url);
    });
  });
  document.querySelectorAll("[data-filter]").forEach((input) => {
    input.addEventListener("input", () => {
      const term = input.value.trim().toLowerCase();
      document.querySelectorAll("[data-name]").forEach((row) => {
        row.hidden = term.length > 0 && !(row.dataset.name || "").toLowerCase().includes(term);
      });
    });
  });
  document.querySelector("[data-enable-push]")?.addEventListener("click", enablePush);
  document.querySelectorAll(".lightbox-link").forEach((link) => {
    link.addEventListener("click", (event) => {
      event.preventDefault();
      const dialog = document.querySelector("dialog.lightbox");
      const image = dialog?.querySelector(".lightbox__image");
      const download = dialog?.querySelector(".lightbox__btn--download");
      if (!dialog || !image)
        return;
      image.src = link.getAttribute("href") || "";
      if (download)
        download.href = image.src + "?download=1";
      dialog.showModal();
    });
  });

  const composer = document.querySelector("#composer");
  const composerForm = document.querySelector("#composer-frame");
  if (composer && composerForm)
    wireComposer(composer, composerForm);

  document.querySelectorAll(".sidebar__toggle").forEach((button) => {
    button.addEventListener("click", () => {
      document.getElementById("sidebar")?.classList.toggle("open");
    });
  });

  document.querySelectorAll(".messages").forEach(presentList);

  const live = document.querySelector("[data-live]");
  if (!live)
    return;
  if (!window.signalR) {
    document.documentElement.dataset.campfireError = "signalr client was not loaded";
    return;
  }

  const roomNode = document.querySelector(".messages[data-room-id]");
  const roomId = roomNode ? Number(roomNode.dataset.roomId) : 0;
  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/campfire")
    .withAutomaticReconnect()
    .build();

  connection.on("Message", (message) => {
    if (!roomNode || Number(message.roomId) !== roomId)
      return;
    appendMessage(message);
  });

  connection.on("Removed", (removal) => {
    if (!roomNode || Number(removal.roomId) !== roomId)
      return;
    document.getElementById("message-" + removal.id)?.remove();
  });

  connection.on("Unread", (unread) => {
    const id = String(unread.roomId ?? "");
    if (!/^[0-9]+$/.test(id))
      return;
    document.querySelectorAll('[data-sidebar-room="' + id + '"]').forEach((item) => {
      item.classList.add("unread");
    });
  });

  const typingNames = new Map();
  connection.on("Typing", (notice) => {
    if (!roomNode || Number(notice.roomId) !== roomId)
      return;
    const key = String(notice.userId || "");
    if (notice.typing)
      typingNames.set(key, notice.name || "Someone");
    else
      typingNames.delete(key);
    const node = document.querySelector("[data-typing]");
    if (!node)
      return;
    const names = [...typingNames.values()];
    node.textContent = names.length === 0 ? "" : names.join(", ") + (names.length === 1 ? " is typing" : " are typing");
  });

  connection.on("Presence", (presence) => {
    if (!roomNode || Number(presence.roomId) !== roomId)
      return;
    const node = document.querySelector("[data-presence]");
    if (!node)
      return;
    const userId = String(presence.userId ?? "");
    if (!/^[0-9]+$/.test(userId))
      return;
    const selector = '.present[data-user-id="' + userId + '"]';
    const person = node.querySelector(selector);
    if (presence.present) {
      if (person)
        return;
      const span = document.createElement("span");
      span.className = "present";
      span.dataset.userId = userId;
      node.append(span);
    } else if (person) {
      person.remove();
    }
  });

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className)
      node.className = className;
    if (text != null)
      node.textContent = text;
    return node;
  }

  function appendMessage(message) {
    const id = "message-" + message.id;
    if (document.getElementById(id))
      return;
    const stamp = isoStamp(message.createdAt);
    const root = element("div", "message message--formatted");
    root.id = id;
    root.dataset.userId = String(message.creatorId || "");
    root.dataset.messageId = String(message.id);
    const day = element("h2", "message__day-separator");
    const date = document.createElement("time");
    date.setAttribute("datetime", stamp);
    date.dataset.localTimeTarget = "date";
    day.append(date);
    root.append(day);

    const figure = element("figure", "avatar message__avatar");
    const link = element("a", "btn avatar");
    link.href = "/users/" + (message.creatorId || "");
    link.title = message.creator || "";
    const image = document.createElement("img");
    image.src = "/assets/images/default-avatar.svg";
    image.alt = "";
    image.width = 48;
    image.height = 48;
    link.append(image);
    figure.append(link);
    root.append(figure);

    const body = element("div", "message__body");
    const content = element("div", "message__body-content");
    const meta = element("div", "message__meta");
    const heading = element("h3", "message__heading");
    const author = element("span", "message__author");
    author.title = message.creator || "";
    author.append(element("strong", "", message.creator || ""));
    const permalink = element("a", "message__permalink");
    permalink.href = "/rooms/" + message.roomId + "/@" + message.id;
    const clock = document.createElement("time");
    clock.className = "message__timestamp";
    clock.setAttribute("datetime", stamp);
    clock.dataset.localTimeTarget = "time";
    permalink.append(clock);
    const room = element("span", "message__room");
    const roomLink = element("a", "", "");
    roomLink.href = permalink.href;
    room.append(roomLink);
    heading.append(author, permalink, room);
    meta.append(heading);
    const presentation = element("div");
    presentation.dir = "auto";
    presentation.dataset.messagesTarget = "body";
    presentation.innerHTML = message.html || "";
    const boosts = element("div", "boosts flex flex-wrap align-center gap full-width");
    content.append(meta, presentation, boosts);
    body.append(content);
    root.append(body);
    const anchor = roomNode.querySelector("[data-presence]") || roomNode.querySelector(".for-screen-reader");
    if (anchor)
      roomNode.insertBefore(root, anchor);
    else
      roomNode.append(root);
    presentList(roomNode);
  }

  connection.start().then(() => {
    if (roomId)
      return connection.invoke("JoinRoom", roomId);
  }).then(() => {
    document.documentElement.dataset.campfireConnected = "1";
    const focus = document.querySelector(".message[data-message-id='" + (location.pathname.split("@").pop() || "") + "']");
    focus?.scrollIntoView({ block: "center" });
    wireTyping(connection, roomId);
  }).catch((error) => {
    document.documentElement.dataset.campfireError = String(error && error.message ? error.message : error);
  });

  function presentList(container) {
    if (!container)
      return;
    const me = document.querySelector('meta[name="current-user-id"]')?.content || "";
    let previous = null;
    container.querySelectorAll(":scope > .message").forEach((message) => {
      const time = message.querySelector("time[datetime]");
      if (!time)
        return;
      const instant = new Date(time.getAttribute("datetime"));
      if (Number.isNaN(instant.getTime()))
        return;
      message.dataset.messageTimestamp = String(instant.getTime());
      const date = message.querySelector('[data-local-time-target="date"]');
      const clock = message.querySelector('[data-local-time-target="time"]');
      if (date)
        date.textContent = formatDate(instant);
      if (clock)
        clock.textContent = formatClock(instant);
      const userId = message.dataset.userId || "";
      message.classList.toggle("message--me", me !== "" && userId === me);
      const recent = previous
        && previous.dataset.userId === userId
        && Math.abs(Number(previous.dataset.messageTimestamp) - instant.getTime()) <= 5 * 60 * 1000;
      message.classList.toggle("message--threaded", !!recent);
      const first = !previous || localDay(new Date(Number(previous.dataset.messageTimestamp))) !== localDay(instant);
      message.classList.toggle("message--first-of-day", first);
      message.classList.add("message--formatted");
      previous = message;
    });
  }

  function isoStamp(value) {
    const instant = new Date(value);
    return Number.isNaN(instant.getTime()) ? "" : instant.toISOString();
  }

  function localDay(instant) {
    return instant.getFullYear() + "-" + instant.getMonth() + "-" + instant.getDate();
  }

  function formatDate(instant) {
    const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    return months[instant.getMonth()] + " " + instant.getDate();
  }

  function formatClock(instant) {
    let hours = instant.getHours();
    const minutes = String(instant.getMinutes()).padStart(2, "0");
    const suffix = hours >= 12 ? "PM" : "AM";
    hours = hours % 12;
    if (hours === 0)
      hours = 12;
    return hours + ":" + minutes + " " + suffix;
  }

  function wireTyping(connection, roomId) {
    const field = document.querySelector("#composer");
    if (!field || !roomId)
      return;
    let timer = 0;
    let active = false;
    field.addEventListener("input", () => {
      if (!active) {
        active = true;
        connection.invoke("Typing", roomId, true).catch(() => {});
      }
      clearTimeout(timer);
      timer = setTimeout(() => {
        active = false;
        connection.invoke("Typing", roomId, false).catch(() => {});
      }, 2500);
    });
  }

  function wireComposer(field, form) {
    const roomId = Number(form.dataset.roomId || 0);
    const file = form.querySelector("input[type=file]");
    const list = form.querySelector("[data-filelist]");
    file?.addEventListener("change", () => {
      if (!list)
        return;
      list.textContent = file.files && file.files[0] ? file.files[0].name : "";
    });

    const rich = form.querySelector("[data-rich-text]");
    const toolbar = form.querySelector("[data-toolbar]");
    rich?.addEventListener("click", () => {
      if (field.dataset.rich === "1")
        return;
      field.dataset.rich = "1";
      field.hidden = true;
      const editor = document.createElement("div");
      editor.className = "input lexxy-content";
      editor.contentEditable = "true";
      editor.dataset.editor = "1";
      editor.setAttribute("role", "textbox");
      editor.setAttribute("aria-label", "Write a message");
      editor.innerText = field.value;
      field.insertAdjacentElement("afterend", editor);
      if (toolbar) {
        toolbar.hidden = false;
        ["bold", "italic", "code", "link", "list"].forEach((name) => {
          const button = document.createElement("button");
          button.type = "button";
          button.className = "btn";
          button.textContent = name;
          button.addEventListener("click", () => format(editor, name));
          toolbar.append(button);
        });
      }
      form.addEventListener("submit", () => {
        field.value = editor.innerHTML;
        field.disabled = false;
      });
    });

    const emoji = form.querySelector("[data-emoji]");
    emoji?.addEventListener("click", async () => {
      let panel = form.querySelector("[data-emoji-panel]");
      if (panel) {
        panel.hidden = !panel.hidden;
        return;
      }
      panel = document.createElement("div");
      panel.dataset.emojiPanel = "1";
      panel.className = "flex flex-wrap gap";
      ["👍", "❤️", "😂", "🎉", "🔥", "👀", "✅", "🙏"].forEach((symbol) => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "btn";
        button.textContent = symbol;
        button.addEventListener("click", () => insertText(field, symbol));
        panel.append(button);
      });
      form.insertBefore(panel, form.firstChild);
      try {
        const response = await fetch("/sounds");
        const sounds = await response.json();
        sounds.forEach((sound) => {
          const button = document.createElement("button");
          button.type = "button";
          button.className = "btn";
          button.textContent = "/" + sound.name;
          button.addEventListener("click", () => {
            field.value = "/play " + sound.name;
          });
          panel.append(button);
        });
      } catch {
        // The sound list is optional. Typing /play still works.
      }
    });

    let mentionTimer = 0;
    field.addEventListener("input", () => {
      clearTimeout(mentionTimer);
      mentionTimer = setTimeout(() => suggest(field, form, roomId), 150);
      scheduleUnfurl(field, form);
    });
    field.addEventListener("keydown", (event) => {
      if (event.key === "Escape")
        form.querySelector("[data-mentions]")?.replaceChildren();
    });
  }

  function format(editor, name) {
    if (name === "bold")
      document.execCommand("bold");
    else if (name === "italic")
      document.execCommand("italic");
    else if (name === "code")
      document.execCommand("insertHTML", false, "<code>" + selection() + "</code>");
    else if (name === "list")
      document.execCommand("insertUnorderedList");
    else if (name === "link") {
      const url = window.prompt("Link URL");
      if (url)
        document.execCommand("createLink", false, url);
    }
    editor.focus();
  }

  function selection() {
    return window.getSelection()?.toString() || "";
  }

  function insertText(field, text) {
    const start = field.selectionStart || field.value.length;
    field.value = field.value.slice(0, start) + text + field.value.slice(field.selectionEnd || start);
    field.focus();
  }

  async function suggest(field, form, roomId) {
    const box = form.querySelector("[data-mentions]");
    if (!box)
      return;
    const match = /(?:^|\s)@([\w .'-]{1,40})$/.exec(field.value.slice(0, field.selectionStart || 0));
    if (!match) {
      box.hidden = true;
      return;
    }
    const response = await fetch(form.dataset.autocomplete + "?room_id=" + roomId + "&query=" + encodeURIComponent(match[1].trim()));
    if (!response.ok) {
      box.hidden = true;
      return;
    }
    const people = await response.json();
    box.replaceChildren();
    people.forEach((person) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "btn";
      button.textContent = person.name;
      button.addEventListener("click", () => {
        const cursor = field.selectionStart || field.value.length;
        const before = field.value.slice(0, cursor).replace(/@([\w .'-]{1,40})$/, "@" + person.name + " ");
        field.value = before + field.value.slice(cursor);
        box.hidden = true;
        field.focus();
      });
      box.append(button);
    });
    box.hidden = people.length === 0;
  }

  let unfurlTimer = 0;
  function scheduleUnfurl(field, form) {
    clearTimeout(unfurlTimer);
    unfurlTimer = setTimeout(async () => {
      const found = field.value.match(/https?:\/\/[^\s<]+/);
      const holder = form.querySelector("[name=unfurl_html]");
      if (!found || !holder || holder.dataset.url === found[0])
        return;
      const token = document.querySelector('meta[name="csrf-token"]')?.content || "";
      const body = new FormData();
      body.set("url", found[0]);
      const response = await fetch(form.dataset.unfurl, { method: "POST", body, headers: { "X-CSRF-Token": token } });
      if (!response.ok)
        return;
      const preview = await response.json();
      holder.value = preview.html || "";
      holder.dataset.url = found[0];
    }, 400);
  }

  async function enablePush() {
    if (!("Notification" in window) || !("serviceWorker" in navigator))
      return;
    const permission = await Notification.requestPermission();
    if (permission !== "granted")
      return;
    const registration = await navigator.serviceWorker.ready;
    const keyResponse = await fetch("/web_push/public_key");
    const key = (await keyResponse.text()).trim();
    const subscription = await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: key ? urlBase64ToBuffer(key) : undefined,
    });
    const json = subscription.toJSON();
    const body = new FormData();
    body.set("endpoint", subscription.endpoint);
    body.set("p256dh", json.keys?.p256dh || "");
    body.set("auth", json.keys?.auth || "");
    await fetch("/users/me/push_subscriptions", { method: "POST", body, credentials: "same-origin" });
  }

  function urlBase64ToBuffer(value) {
    const padded = value + "=".repeat((4 - value.length % 4) % 4);
    const binary = atob(padded.replace(/-/g, "+").replace(/_/g, "/"));
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++)
      bytes[i] = binary.charCodeAt(i);
    return bytes;
  }
})();
