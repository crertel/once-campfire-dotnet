(() => {
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
    permalink.href = "/rooms/" + message.roomId;
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
})();
