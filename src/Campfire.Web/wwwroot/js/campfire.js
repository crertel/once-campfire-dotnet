(() => {
  const live = document.querySelector("[data-live]");
  if (!live)
    return;
  if (!window.signalR) {
    document.documentElement.dataset.campfireError = "signalr client was not loaded";
    return;
  }

  const roomNode = document.querySelector("#messages[data-room-id]");
  const roomId = roomNode ? Number(roomNode.dataset.roomId) : 0;
  const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/campfire")
    .withAutomaticReconnect()
    .build();

  connection.on("Message", (message) => {
    if (!roomNode || Number(message.roomId) !== roomId)
      return;
    const id = "message-" + message.id;
    if (document.getElementById(id))
      return;
    const article = document.createElement("article");
    article.className = "message";
    article.id = id;
    const author = document.createElement("p");
    author.className = "author";
    author.textContent = message.creator || "";
    const body = document.createElement("div");
    body.className = "body";
    body.innerHTML = message.html || "";
    article.append(author, body);
    roomNode.append(article);
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
      if (item.querySelector(".unread"))
        return;
      const mark = document.createElement("span");
      mark.className = "unread";
      mark.textContent = "unread";
      item.append(mark);
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
    const selector = '[data-user-id="' + userId + '"]';
    const person = node.querySelector(selector);
    if (presence.present) {
      if (person)
        return;
      const span = document.createElement("span");
      span.className = "present";
      span.dataset.userId = userId;
      span.textContent = userId;
      node.append(span);
    } else if (person) {
      person.remove();
    }
  });

  connection.start().then(() => {
    if (roomId)
      return connection.invoke("JoinRoom", roomId);
  }).then(() => {
    document.documentElement.dataset.campfireConnected = "1";
  }).catch((error) => {
    document.documentElement.dataset.campfireError = String(error && error.message ? error.message : error);
  });
})();
