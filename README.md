HTTP Parser
===========

This is a parser for HTTP messages written in C#. It parses both requests and
responses. The parser is designed to be used in performance HTTP
applications. It does not make any syscalls nor allocations, it does not
buffer data, it can be interrupted at anytime. Depending on your
architecture, it only requires about 40 bytes of data per message
stream (in a web server that is per connection).

Features:

  * No dependencies
  * Handles persistent streams (keep-alive).
  * Decodes chunked encoding.
  * Upgrade support
  * Defends against buffer overflow attacks.

The parser extracts the following information from HTTP messages:

  * Header fields and values
  * Content-Length
  * Request method
  * Response status code
  * Transfer-Encoding
  * HTTP version
  * Request URL
  * Message body


Usage
-----

One `HttpParser` object is used per TCP connection. Initialize the class 
and pass in the callbacks via the HttpParserSettings object. That might look 
something like this for a request parser:

    HttpParserSettings settings;
    settings.Url = MyUrlCallback;
    settings.HeaderField = MyHeaderFieldCallback;
    /* ... */

    HttpParser parser = new HttpParser(settings, HttpParserType.HTTP_REQUEST);
    parser->data = my_socket;

When data is received on the socket execute the parser and check for errors.

    size_t len = 80*1024, nparsed;
    char buf[len];
    ssize_t recved;

    recved = recv(fd, buf, len, 0);

    if (recved < 0) {
      /* Handle error. */
    }

    /* Start up / continue the parser.
     * Note we pass recved==0 to signal that EOF has been recieved.
     */
    nparsed = parser.Execute(buf, recved);

    if (parser->upgrade) {
      /* handle new protocol */
    } else if (nparsed != recved) {
      /* Handle error. Usually just close the connection. */
    }

HTTP needs to know where the end of the stream is. For example, sometimes
servers send responses without Content-Length and expect the client to
consume input (for the body) until EOF. To tell HttpParser about EOF, give
`0` as the second parameter to `Execute()`. Callbacks and errors
can still be encountered during an EOF, so one must still be prepared
to receive them.

Scalar valued message information such as `StatusCode`, `Method`, and the
HTTP version are stored in the parser class. This data is only
temporally stored in `HttpParser` and gets reset on each new message. If
this information is needed later, copy it out of the structure during the
`HeadersComplete` callback.

The parser decodes the transfer-encoding for both requests and responses
transparently. That is, a chunked encoding is decoded before being sent to
the Body callback.


The Special Problem of Upgrade
------------------------------

HTTP supports upgrading the connection to a different protocol. An
increasingly common example of this is the Web Socket protocol which sends
a request like

        GET /demo HTTP/1.1
        Upgrade: WebSocket
        Connection: Upgrade
        Host: example.com
        Origin: http://example.com
        WebSocket-Protocol: sample

followed by non-HTTP data.

(See http://tools.ietf.org/html/draft-hixie-thewebsocketprotocol-75 for more
information the Web Socket protocol.)

To support this, the parser will treat this as a normal HTTP message without a
body. Issuing both HeadersComplete and MessageComplete callbacks. However
Execute() will stop parsing at the end of the headers and return.

The user is expected to check if `parser.Upgrade` has been set to true after
`Execute()` returns. Non-HTTP data begins at the buffer supplied
offset by the return value of `Execute()`.


Callbacks
---------

During the `Execute()` call, the callbacks set in
`HttpParserSettings` will be executed. The parser maintains state and
never looks behind, so buffering the data is not necessary. If you need to
save certain data for later usage, you can do that from the callbacks.

There are two types of callbacks:

* notification `delegate int Notification (HttpParser);`
    Callbacks: MessageBegin, HeadersComplete, MessageComplete.
* data `delegate int Data(HttpParser, byte[]);`
    Callbacks: (requests only) Url,
               (common) HeaderField, HeaderValue, Body;

Callbacks must return false on success. Returning true indicates error
to the parser, making it exit immediately.

In case you parse HTTP message in chunks (i.e. `read()` request line
from socket, parse, read half headers, parse, etc) your data callbacks
may be called more than once. HttpParser guarantees that data pointer is only
valid for the lifetime of callback. You can also `read()` into a heap allocated
buffer to avoid copying memory around if this fits your application.

Reading headers may be a tricky task if you read/parse headers partially.
Basically, you need to remember whether last header callback was field or value
and apply following logic:

     ------------------------ ------------- --------------------------------------------
    | State (prev. callback) | Callback    | Description/action                         |
     ------------------------ ------------- --------------------------------------------
    | nothing (first call)   | HeaderField | Allocate new buffer and copy callback data |
    |                        |             | into it                                    |
     ------------------------ ------------- --------------------------------------------
    | value                  | HeaderField | New header started.                        |
    |                        |             | Copy current name,value buffers to headers |
    |                        |             | list and allocate new buffer for new name  |
     ------------------------ ------------- --------------------------------------------
    | field                  | HeaderField | Previous name continues. Reallocate name   |
    |                        |             | buffer and append callback data to it      |
     ------------------------ ------------- --------------------------------------------
    | field                  | HeaderValue | Value for current header started. Allocate |
    |                        |             | new buffer and copy callback data to it    |
     ------------------------ ------------- --------------------------------------------
    | value                  | HeaderValue | Value continues. Reallocate value buffer   |
    |                        |             | and append callback data to it             |
     ------------------------ ------------- --------------------------------------------


See examples of reading in headers:

* [partial example](http://gist.github.com/155877) in C
* [from http-parser tests](http://github.com/ry/http-parser/blob/37a0ff8928fb0d83cec0d0d8909c5a4abcd221af/test.c#L403) in C
* [from Node library](http://github.com/ry/node/blob/842eaf446d2fdcb33b296c67c911c32a0dabc747/src/http.js#L284) in Javascript
