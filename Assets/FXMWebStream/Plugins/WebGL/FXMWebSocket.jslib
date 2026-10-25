// WebSocket plugin for WebGL.
// The browser handles the handshake, TLS, ping/pong, and framing; C# only:
//   - calls FXM_WS_Poll() to pull binary messages from the queue (every frame)
//   - calls FXM_WS_Send() to send a frame
//
// Note: serve the WebGL page over http(s). When the page uses HTTPS, use wss://
// so the browser does not block the mixed-content connection.

mergeInto(LibraryManager.library, {
  $FXMWebSockets: {
    nextId: 1,
    sockets: {}
  },

  FXM_WS_Connect: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    var id = FXMWebSockets.nextId++;

    try {
      var socket = new WebSocket(url);
      socket.binaryType = 'arraybuffer';

      var entry = {
        socket: socket,
        queue: [],
        state: 0, // 0 = connecting, 1 = open, 2 = closed/error
        error: null
      };

      FXMWebSockets.sockets[id] = entry;

      socket.onopen = function () {
        entry.state = 1;
      };

      socket.onmessage = function (event) {
        if (typeof event.data === 'string') {
          return; // ignore text messages
        }

        entry.queue.push(new Uint8Array(event.data));

        // Bound the queue so the tab does not run out of memory when the main thread lags.
        while (entry.queue.length > 32) {
          entry.queue.shift();
        }
      };

      socket.onerror = function () {
        entry.error = 'websocket error';
      };

      socket.onclose = function () {
        entry.state = 2;
      };

      return id;
    } catch (error) {
      console.error('[FXM.WebStream] WebSocket connect failed: ' + error);
      return -1;
    }
  },

  FXM_WS_Poll: function (id, ptr, capacity) {
    var entry = FXMWebSockets.sockets[id];
    if (!entry) {
      return -2;
    }

    if (entry.queue.length === 0) {
      return entry.state === 2 ? -2 : 0;
    }

    var data = entry.queue.shift();
    if (data.length > capacity) {
      // Message exceeds the C# buffer: drop it so the queue does not stall.
      console.warn('[FXM.WebStream] WebSocket message ' + data.length + ' bytes exceeds ' + capacity + ' byte buffer; dropped.');
      return -1;
    }

    HEAPU8.set(data, ptr);
    return data.length;
  },

  FXM_WS_Send: function (id, ptr, length) {
    var entry = FXMWebSockets.sockets[id];
    if (!entry || entry.state !== 1) {
      return 0;
    }

    try {
      // slice() copies data out of HEAP so it stays valid after this function returns.
      entry.socket.send(HEAPU8.subarray(ptr, ptr + length).slice());
      return 1;
    } catch (error) {
      console.warn('[FXM.WebStream] WebSocket send failed: ' + error);
      return 0;
    }
  },

  FXM_WS_GetState: function (id) {
    var entry = FXMWebSockets.sockets[id];
    if (!entry) {
      return 2;
    }

    return entry.state;
  },

  FXM_WS_Close: function (id) {
    var entry = FXMWebSockets.sockets[id];
    if (!entry) {
      return;
    }

    try {
      entry.socket.close();
    } catch (error) {
      // Ignored.
    }

    delete FXMWebSockets.sockets[id];
  }
});
