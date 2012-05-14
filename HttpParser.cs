/* Copyright Joyent, Inc. and other Node contributors. All rights reserved.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to
 * deal in the Software without restriction, including without limitation the
 * rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
 * sell copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
 * IN THE SOFTWARE.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HttpParser
{
    public class HttpParser
    {
        private const ulong HTTP_MAX_HEADER_SIZE = (80*1024);

        private const string PROXY_CONNECTION = "proxy-connection";
        private const string CONNECTION = "connection";
        private const string CONTENT_LENGTH = "content-length";
        private const string TRANSFER_ENCODING = "transfer-encoding";
        private const string UPGRADE = "upgrade";
        private const string CHUNKED = "chunked";
        private const string KEEP_ALIVE = "keep-alive";
        private const string CLOSE = "close";

        private HttpParserSettings _settings;

        private State _state;
        private HeaderStates _header_state;
        private int _index;
        private ulong _nread;
        private Flags _flags;
        private long _content_length;
        private HttpParserType _type;

        /** READ-ONLY **/
        private short _http_major;
        private short _http_minor;
        private short _status_code; /* responses only */
        private HttpMethod _method; /* requests only */
        private HttpErrNo _http_errno;

        /* 1 = Upgrade header was present and the parser has exited because of that.
         * 0 = No upgrade header present.
         * Should be checked when http_parser_execute() returns in addition to
         * error checking.
         */
        private byte _upgrade = 1;

        public HttpParser(HttpParserSettings settings, HttpParserType t)
        {
            _settings = settings;
            _settings.Wrap();

            _type = t;
            _state = (t == HttpParserType.HTTP_REQUEST ? State.s_start_req : (t == HttpParserType.HTTP_RESPONSE ? State.s_start_res : State.s_start_req_or_res));
            _nread = 0;
            _upgrade = 0;
            _flags = 0;
            _method = 0;
            _http_errno = HttpErrNo.OK;
        }

        #region Properties
        public HttpMethod Method
        {
            get
            {
                return _method;
            }
        }

        public HttpErrNo ErrNo
        {
            get
            {
                return _http_errno;
            }
        }

        public short HttpMajor
        {
            get
            {
                return _http_major;
            }
        }

        public short HttpMinor
        {
            get
            {
                return _http_minor;
            }
        }

        public short StatusCode
        {
            get
            {
                return _status_code;
            }
        }

        public bool Upgrade
        {
            get
            {
                return _upgrade == 1;
            }
        }
        #endregion

        private void SET_ERRNO(HttpErrNo http_errno)
        {
            _http_errno = http_errno;
        }

        private bool PARSING_HEADER(State state)
        {
            return state <= State.s_headers_almost_done;
        }

        private static char LOWER(char c)
        {
            return (char)(c | 0x20);
        }

        private static char TOKEN(char c)
        {
            return LookupTables.tokens[c];
        }

        private static bool IS_ALPHA(char c)
        {
            return (LOWER(c) >= 'a' && LOWER(c) <= 'z');
        }

        private static bool IS_NUM(char c)
        {
            return (c >= '0' && c <= '9');
        }

        private static bool IS_ALPHANUM(char c)
        {
            return (IS_ALPHA(c) || IS_NUM(c));
        }

        private static long MIN(long a, long b)
        {
            return ((a) < (b) ? (a) : (b));
        }

#if HTTP_PARSER_STRICT
        private bool IS_URL_CHAR(char c)
        {
            return(LookupTables.normal_url_char[c] != 0);
        }
#else
        private bool IS_URL_CHAR(char c)
        {
            return (LookupTables.normal_url_char[c] != 0 || ((c & 0x80) != 0));
        }
#endif

#if HTTP_PARSER_STRICT
        private bool IS_HOST_CHAR(char c) {
            return (IS_ALPHANUM(c) || (c) == '.' || (c) == '-');
        }
#else
        private bool IS_HOST_CHAR(char c)
        {
            return (IS_ALPHANUM(c) || (c) == '.' || (c) == '-' || (c) == '_');
        }
#endif

        private State start_state()
        {
            return (_type == HttpParserType.HTTP_REQUEST ? State.s_start_req : State.s_start_res);
        }

#if HTTP_PARSER_STRICT
        private void STRICT_CHECK(bool cond)
        {
            if (cond)
            {
                SET_ERRNO(HttpErrNo.STRICT);
                throw new ParseException();
            }
        }
#else
        private void STRICT_CHECK(bool cond) { }
#endif

#if HTTP_PARSER_STRICT
        private State NEW_MESSAGE()
        {
            return (http_should_keep_alive() != 0 ? start_state() : State.s_dead);
        }
#else
        private State NEW_MESSAGE()
        {
            return start_state();
        }
#endif

        public unsafe uint Execute(byte[] data, int length)
        {
            fixed (byte* p = data)
            {
                return Execute(p, (uint)length);
            }
        }

        public unsafe uint Execute(byte* data, uint len)
        {
            char c, ch;
            sbyte unhex_val;
            byte* p = data;
            byte *pe;
            long to_read;
            State state;
            HeaderStates header_state;
            int index = _index;
            ulong nread = _nread;

            /* technically we could combine all of these (except for url_mark) into one
            variable, saving stack space, but it seems more clear to have them
            separated. */
            byte *header_field_mark = null;
            byte *header_value_mark = null;
            byte *url_mark = null;

            /* We're in an error state. Don't bother doing anything. */
            if (_http_errno != HttpErrNo.OK) 
            {
                return 0;
            }

            state = _state;
            header_state = _header_state;

            if (len == 0)
            {
                switch (state)
                {
                    case State.s_body_identity_eof:
                        if (_settings.on_message_complete != null)
                        {
                            if (0 != _settings.on_message_complete(this))
                            {
                                SET_ERRNO(HttpErrNo.CB_message_complete);
                                return (uint)(p - data);
                            }
                        }
                        return 0;

                    case State.s_dead:
                    case State.s_start_req_or_res:
                    case State.s_start_res:
                    case State.s_start_req:
                        return 0;

                    default:
                        SET_ERRNO(HttpErrNo.INVALID_EOF_STATE);
                        return 1;
                }
            }

            if (state == State.s_header_field)
                header_field_mark = data;
            if (state == State.s_header_value)
                header_value_mark = data;
            if (state == State.s_req_path || state == State.s_req_schema || state == State.s_req_schema_slash
                || state == State.s_req_schema_slash_slash || state == State.s_req_port
                || state == State.s_req_query_string_start || state == State.s_req_query_string
                || state == State.s_req_host
                || state == State.s_req_fragment_start || state == State.s_req_fragment)
                url_mark = data;

            try
            {
                for (p = data, pe = data + len; p != pe; p++)
                {
                    ch = (char)*p;

                    if (PARSING_HEADER(state))
                    {
                        ++nread;
                        /* Buffer overflow attack */
                        if (nread > HTTP_MAX_HEADER_SIZE)
                        {
                            SET_ERRNO(HttpErrNo.HEADER_OVERFLOW);
                            throw new ParseException();
                        }
                    }

                    switch (state)
                    {

                        case State.s_dead:
                            /* this state is used after a 'Connection: close' message
                             * the parser will error out if it reads another message
                             */
                            SET_ERRNO(HttpErrNo.CLOSED_CONNECTION);
                            throw new ParseException();

                        case State.s_start_req_or_res:
                            if (ch == '\r' || ch == '\n')
                                break;
                            _flags = 0;
                            _content_length = -1;

                            if (_settings.on_message_begin != null)
                            {
                                if (0 != _settings.on_message_begin(this))
                                {
                                    SET_ERRNO(HttpErrNo.CB_message_begin);
                                    return (uint)(p - data);
                                }
                            }

                            if (ch == 'H')
                                state = State.s_res_or_resp_H;
                            else
                            {
                                _type = HttpParserType.HTTP_REQUEST;
                                goto start_req_method_assign;
                            }
                            break;

                        case State.s_res_or_resp_H:
                            if (ch == 'T')
                            {
                                _type = HttpParserType.HTTP_RESPONSE;
                                state = State.s_res_HT;
                            }
                            else
                            {
                                if (ch != 'E')
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_CONSTANT);
                                    throw new ParseException();
                                }

                                _type = HttpParserType.HTTP_REQUEST;
                                _method = HttpMethod.HTTP_HEAD;
                                index = 2;
                                state = State.s_req_method;
                            }
                            break;

                        case State.s_start_res:
                            {
                                _flags = 0;
                                _content_length = -1;

                                if (_settings.on_message_begin != null)
                                {
                                    if (0 != _settings.on_message_begin(this))
                                    {
                                        SET_ERRNO(HttpErrNo.CB_message_begin);
                                        return (uint)(p - data);
                                    }
                                }

                                switch (ch)
                                {
                                    case 'H':
                                        state = State.s_res_H;
                                        break;

                                    case '\r':
                                    case '\n':
                                        break;

                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_CONSTANT);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_res_H:
                            STRICT_CHECK(ch != 'T');
                            state = State.s_res_HT;
                            break;

                        case State.s_res_HT:
                            STRICT_CHECK(ch != 'T');
                            state = State.s_res_HTT;
                            break;

                        case State.s_res_HTT:
                            STRICT_CHECK(ch != 'P');
                            state = State.s_res_HTTP;
                            break;

                        case State.s_res_HTTP:
                            STRICT_CHECK(ch != '/');
                            state = State.s_res_first_http_major;
                            break;

                        case State.s_res_first_http_major:
                            if (ch < '0' || ch > '9')
                            {
                                SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                throw new ParseException();
                            }

                            _http_major = (short)(ch - '0');
                            state = State.s_res_http_major;
                            break;

                        /* major HTTP version or dot */
                        case State.s_res_http_major:
                            {
                                if (ch == '.')
                                {
                                    state = State.s_res_first_http_minor;
                                    break;
                                }

                                if (!IS_NUM(ch))
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                _http_major = (short)(_http_major * 10);
                                _http_major = (short)(_http_major + (ch - '0'));

                                if (_http_major > 999)
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                break;
                            }

                        /* first digit of minor HTTP version */
                        case State.s_res_first_http_minor:
                            if (!IS_NUM(ch))
                            {
                                SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                throw new ParseException();
                            }

                            _http_minor = (short)(ch - '0');
                            state = State.s_res_http_minor;
                            break;

                        /* minor HTTP version or end of request line */
                        case State.s_res_http_minor:
                            {
                                if (ch == ' ')
                                {
                                    state = State.s_res_first_status_code;
                                    break;
                                }

                                if (!IS_NUM(ch))
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                _http_minor *= 10;
                                _http_minor += (short)(ch - '0');

                                if (_http_minor > 999)
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                break;
                            }

                        case State.s_res_first_status_code:
                            {
                                if (!IS_NUM(ch))
                                {
                                    if (ch == ' ')
                                    {
                                        break;
                                    }

                                    SET_ERRNO(HttpErrNo.INVALID_STATUS);
                                    throw new ParseException();
                                }
                                _status_code = (short)(ch - '0');
                                state = State.s_res_status_code;
                                break;
                            }

                        case State.s_res_status_code:
                            {
                                if (!IS_NUM(ch))
                                {
                                    switch (ch)
                                    {
                                        case ' ':
                                            state = State.s_res_status;
                                            break;
                                        case '\r':
                                            state = State.s_res_line_almost_done;
                                            break;
                                        case '\n':
                                            state = State.s_header_field_start;
                                            break;
                                        default:
                                            SET_ERRNO(HttpErrNo.INVALID_STATUS);
                                            throw new ParseException();
                                    }
                                    break;
                                }

                                _status_code *= 10;
                                _status_code += (short)(ch - '0');

                                if (_status_code > 999)
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_STATUS);
                                    throw new ParseException();
                                }

                                break;
                            }

                        case State.s_res_status:
                            /* the human readable status. e.g. "NOT FOUND"
                             * we are not humans so just ignore this */
                            if (ch == '\r')
                            {
                                state = State.s_res_line_almost_done;
                                break;
                            }

                            if (ch == '\n')
                            {
                                state = State.s_header_field_start;
                                break;
                            }
                            break;

                        case State.s_res_line_almost_done:
                            STRICT_CHECK(ch != '\n');
                            state = State.s_header_field_start;
                            break;

                        case State.s_start_req:
                            if (ch == '\r' || ch == '\n')
                                break;
                            _flags = 0;
                            _content_length = -1;

                            if (_settings.on_message_begin != null)
                            {
                                if (0 != _settings.on_message_begin(this))
                                {
                                    SET_ERRNO(HttpErrNo.CB_message_begin);
                                    return (uint)(p - data);
                                }
                            }

                            if (!IS_ALPHA(ch))
                            {
                                SET_ERRNO(HttpErrNo.INVALID_METHOD);
                                throw new ParseException();
                            }

start_req_method_assign:
                            _method = (HttpMethod)0;
                            index = 1;
                            switch (ch)
                            {
                                case 'C': _method = HttpMethod.HTTP_CONNECT; /* or COPY, CHECKOUT */ break;
                                case 'D': _method = HttpMethod.HTTP_DELETE; break;
                                case 'G': _method = HttpMethod.HTTP_GET; break;
                                case 'H': _method = HttpMethod.HTTP_HEAD; break;
                                case 'L': _method = HttpMethod.HTTP_LOCK; break;
                                case 'M': _method = HttpMethod.HTTP_MKCOL; /* or MOVE, MKACTIVITY, MERGE, M-SEARCH */ break;
                                case 'N': _method = HttpMethod.HTTP_NOTIFY; break;
                                case 'O': _method = HttpMethod.HTTP_OPTIONS; break;
                                case 'P': _method = HttpMethod.HTTP_POST;
                                    /* or PROPFIND or PROPPATCH or PUT or PATCH */
                                    break;
                                case 'R': _method = HttpMethod.HTTP_REPORT; break;
                                case 'S': _method = HttpMethod.HTTP_SUBSCRIBE; break;
                                case 'T': _method = HttpMethod.HTTP_TRACE; break;
                                case 'U': _method = HttpMethod.HTTP_UNLOCK; /* or UNSUBSCRIBE */ break;
                                default:
                                    SET_ERRNO(HttpErrNo.INVALID_METHOD);
                                    throw new ParseException();
                            }
                            state = State.s_req_method;
                            break;

                        case State.s_req_method:
                            {
                                if (ch == '\0')
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_METHOD);
                                    throw new ParseException();
                                }

                                fixed(char *matcher = LookupTables.method_strings[(int)_method]) {
                                if (ch == ' ' && matcher[index] == '\0')
                                {
                                    state = State.s_req_spaces_before_url;
                                }
                                else if (ch == matcher[index])
                                {
                                    ; /* nada */
                                }
                                else if (_method == HttpMethod.HTTP_CONNECT)
                                {
                                    if (index == 1 && ch == 'H')
                                    {
                                        _method = HttpMethod.HTTP_CHECKOUT;
                                    }
                                    else if (index == 2 && ch == 'P')
                                    {
                                        _method = HttpMethod.HTTP_COPY;
                                    }
                                    else
                                    {
                                        throw new ParseException();
                                    }
                                }
                                else if (_method == HttpMethod.HTTP_MKCOL)
                                {
                                    if (index == 1 && ch == 'O')
                                    {
                                        _method = HttpMethod.HTTP_MOVE;
                                    }
                                    else if (index == 1 && ch == 'E')
                                    {
                                        _method = HttpMethod.HTTP_MERGE;
                                    }
                                    else if (index == 1 && ch == '-')
                                    {
                                        _method = HttpMethod.HTTP_MSEARCH;
                                    }
                                    else if (index == 2 && ch == 'A')
                                    {
                                        _method = HttpMethod.HTTP_MKACTIVITY;
                                    }
                                    else
                                    {
                                        throw new ParseException();
                                    }
                                }
                                else if (index == 1 && _method == HttpMethod.HTTP_POST)
                                {
                                    if (ch == 'R')
                                    {
                                        _method = HttpMethod.HTTP_PROPFIND; /* or HttpMethod.HTTP_PROPPATCH */
                                    }
                                    else if (ch == 'U')
                                    {
                                        _method = HttpMethod.HTTP_PUT;
                                    }
                                    else if (ch == 'A')
                                    {
                                        _method = HttpMethod.HTTP_PATCH;
                                    }
                                    else
                                    {
                                        throw new ParseException();
                                    }
                                }
                                else if (index == 2 && _method == HttpMethod.HTTP_UNLOCK && ch == 'S')
                                {
                                    _method = HttpMethod.HTTP_UNSUBSCRIBE;
                                }
                                else if (index == 4 && _method == HttpMethod.HTTP_PROPFIND && ch == 'P')
                                {
                                    _method = HttpMethod.HTTP_PROPPATCH;
                                }
                                else
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_METHOD);
                                    throw new ParseException();
                                }
                                }

                                ++index;
                                break;
                            }

                        case State.s_req_spaces_before_url:
                            {
                                if (ch == ' ') break;

                                if (ch == '/' || ch == '*')
                                {
                                    url_mark = p;
                                    state = State.s_req_path;
                                    break;
                                }

                                /* Proxied requests are followed by scheme of an absolute URI (alpha).
                                 * CONNECT is followed by a hostname, which begins with alphanum.
                                 * All other methods are followed by '/' or '*' (handled above).
                                 */
                                if (IS_ALPHA(ch) || (_method == HttpMethod.HTTP_CONNECT && IS_NUM(ch)))
                                {
                                    url_mark = p;
                                    state = (_method == HttpMethod.HTTP_CONNECT) ? State.s_req_host : State.s_req_schema;
                                    break;
                                }

                                SET_ERRNO(HttpErrNo.INVALID_URL);
                                throw new ParseException();
                            }

                        case State.s_req_schema:
                            {
                                if (IS_ALPHA(ch)) break;

                                if (ch == ':')
                                {
                                    state = State.s_req_schema_slash;
                                    break;
                                }

                                SET_ERRNO(HttpErrNo.INVALID_URL);
                                throw new ParseException();
                            }

                        case State.s_req_schema_slash:
                            STRICT_CHECK(ch != '/');
                            state = State.s_req_schema_slash_slash;
                            break;

                        case State.s_req_schema_slash_slash:
                            STRICT_CHECK(ch != '/');
                            state = State.s_req_host;
                            break;

                        case State.s_req_host:
                            {
                                if (IS_HOST_CHAR(ch)) break;
                                switch (ch)
                                {
                                    case ':':
                                        state = State.s_req_port;
                                        break;
                                    case '/':
                                        state = State.s_req_path;
                                        break;
                                    case ' ':
                                        /* The request line looks like:
                                         *   "GET http://foo.bar.com HTTP/1.1"
                                         * That is, there is no path.
                                         */
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        state = State.s_req_http_start;
                                        break;
                                    case '?':
                                        state = State.s_req_query_string_start;
                                        break;
                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_HOST);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_req_port:
                            {
                                if (IS_NUM(ch)) break;
                                switch (ch)
                                {
                                    case '/':
                                        state = State.s_req_path;
                                        break;
                                    case ' ':
                                        /* The request line looks like:
                                         *   "GET http://foo.bar.com:1234 HTTP/1.1"
                                         * That is, there is no path.
                                         */
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        state = State.s_req_http_start;
                                        break;
                                    case '?':
                                        state = State.s_req_query_string_start;
                                        break;
                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_PORT);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_req_path:
                            {
                                if (IS_URL_CHAR(ch)) break;

                                switch (ch)
                                {
                                    case ' ':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        state = State.s_req_http_start;
                                        break;
                                    case '\r':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_req_line_almost_done;
                                        break;
                                    case '\n':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_header_field_start;
                                        break;
                                    case '?':
                                        state = State.s_req_query_string_start;
                                        break;
                                    case '#':
                                        state = State.s_req_fragment_start;
                                        break;
                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_PATH);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_req_query_string_start:
                            {
                                if (IS_URL_CHAR(ch))
                                {
                                    state = State.s_req_query_string;
                                    break;
                                }

                                switch (ch)
                                {
                                    case '?':
                                        break; /* XXX ignore extra '?' ... is this right? */
                                    case ' ':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        state = State.s_req_http_start;
                                        break;
                                    case '\r':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        };
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_req_line_almost_done;
                                        break;
                                    case '\n':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_header_field_start;
                                        break;
                                    case '#':
                                        state = State.s_req_fragment_start;
                                        break;
                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_QUERY_STRING);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_req_query_string:
                            {
                                if (IS_URL_CHAR(ch)) break;

                                switch (ch)
                                {
                                    case '?':
                                        /* allow extra '?' in query string */
                                        break;
                                    case ' ':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        state = State.s_req_http_start;
                                        break;
                                    case '\r':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_req_line_almost_done;
                                        break;
                                    case '\n':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_header_field_start;
                                        break;
                                    case '#':
                                        state = State.s_req_fragment_start;
                                        break;
                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_QUERY_STRING);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_req_fragment_start:
                            {
                                if (IS_URL_CHAR(ch))
                                {
                                    state = State.s_req_fragment;
                                    break;
                                }

                                switch (ch)
                                {
                                    case ' ':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        state = State.s_req_http_start;
                                        break;
                                    case '\r':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_req_line_almost_done;
                                        break;
                                    case '\n':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_header_field_start;
                                        break;
                                    case '?':
                                        state = State.s_req_fragment;
                                        break;
                                    case '#':
                                        break;
                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_FRAGMENT);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_req_fragment:
                            {
                                if (IS_URL_CHAR(ch)) break;

                                switch (ch)
                                {
                                    case ' ':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        state = State.s_req_http_start;
                                        break;
                                    case '\r':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_req_line_almost_done;
                                        break;
                                    case '\n':
                                        if (url_mark != null)
                                        {
                                            if (_settings.on_url != null)
                                            {
                                                if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                                                {
                                                    SET_ERRNO(HttpErrNo.CB_url);
                                                    return (uint)(p - data);
                                                }
                                            }
                                            url_mark = null;
                                        }
                                        _http_major = 0;
                                        _http_minor = 9;
                                        state = State.s_header_field_start;
                                        break;
                                    case '?':
                                    case '#':
                                        break;
                                    default:
                                        SET_ERRNO(HttpErrNo.INVALID_FRAGMENT);
                                        throw new ParseException();
                                }
                                break;
                            }

                        case State.s_req_http_start:
                            switch (ch)
                            {
                                case 'H':
                                    state = State.s_req_http_H;
                                    break;
                                case ' ':
                                    break;
                                default:
                                    SET_ERRNO(HttpErrNo.INVALID_CONSTANT);
                                    throw new ParseException();
                            }
                            break;

                        case State.s_req_http_H:
                            STRICT_CHECK(ch != 'T');
                            state = State.s_req_http_HT;
                            break;

                        case State.s_req_http_HT:
                            STRICT_CHECK(ch != 'T');
                            state = State.s_req_http_HTT;
                            break;

                        case State.s_req_http_HTT:
                            STRICT_CHECK(ch != 'P');
                            state = State.s_req_http_HTTP;
                            break;

                        case State.s_req_http_HTTP:
                            STRICT_CHECK(ch != '/');
                            state = State.s_req_first_http_major;
                            break;

                        /* first digit of major HTTP version */
                        case State.s_req_first_http_major:
                            if (ch < '1' || ch > '9')
                            {
                                SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                throw new ParseException();
                            }

                            _http_major = (short)(ch - '0');
                            state = State.s_req_http_major;
                            break;

                        /* major HTTP version or dot */
                        case State.s_req_http_major:
                            {
                                if (ch == '.')
                                {
                                    state = State.s_req_first_http_minor;
                                    break;
                                }

                                if (!IS_NUM(ch))
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                _http_major *= 10;
                                _http_major += (short)(ch - '0');

                                if (_http_major > 999)
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                break;
                            }

                        /* first digit of minor HTTP version */
                        case State.s_req_first_http_minor:
                            if (!IS_NUM(ch))
                            {
                                SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                throw new ParseException();
                            }

                            _http_minor = (short)(ch - '0');
                            state = State.s_req_http_minor;
                            break;

                        /* minor HTTP version or end of request line */
                        case State.s_req_http_minor:
                            {
                                if (ch == '\r')
                                {
                                    state = State.s_req_line_almost_done;
                                    break;
                                }

                                if (ch == '\n')
                                {
                                    state = State.s_header_field_start;
                                    break;
                                }

                                /* XXX allow spaces after digit? */

                                if (!IS_NUM(ch))
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                _http_minor *= 10;
                                _http_minor += (short)(ch - '0');

                                if (_http_minor > 999)
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_VERSION);
                                    throw new ParseException();
                                }

                                break;
                            }

                        /* end of request line */
                        case State.s_req_line_almost_done:
                            {
                                if (ch != '\n')
                                {
                                    SET_ERRNO(HttpErrNo.LF_EXPECTED);
                                    throw new ParseException();
                                }

                                state = State.s_header_field_start;
                                break;
                            }

                        case State.s_header_field_start:
header_field_start:
                            {
                                if (ch == '\r')
                                {
                                    state = State.s_headers_almost_done;
                                    break;
                                }

                                if (ch == '\n')
                                {
                                    /* they might be just sending \n instead of \r\n so this would be
                                     * the second \n to denote the end of headers*/
                                    state = State.s_headers_almost_done;
                                    goto headers_almost_done;
                                }

                                c = TOKEN(ch);

                                if (c == 0)
                                {
                                    SET_ERRNO(HttpErrNo.INVALID_HEADER_TOKEN);
                                    throw new ParseException();
                                }

                                header_field_mark = p;

                                index = 0;
                                state = State.s_header_field;

                                switch (c)
                                {
                                    case 'c':
                                        header_state = HeaderStates.h_C;
                                        break;

                                    case 'p':
                                        header_state = HeaderStates.h_matching_proxy_connection;
                                        break;

                                    case 't':
                                        header_state = HeaderStates.h_matching_transfer_encoding;
                                        break;

                                    case 'u':
                                        header_state = HeaderStates.h_matching_upgrade;
                                        break;

                                    default:
                                        header_state = HeaderStates.h_general;
                                        break;
                                }
                                break;
                            }

                        case State.s_header_field:
                        {
                            c = TOKEN(ch);

                            if (c != 0)
                            {
                                switch (header_state)
                                {
                                    case HeaderStates.h_general:
                                        break;

                                    case HeaderStates.h_C:
                                        index++;
                                        header_state = (c == 'o' ? HeaderStates.h_CO : HeaderStates.h_general);
                                        break;

                                    case HeaderStates.h_CO:
                                        index++;
                                        header_state = (c == 'n' ? HeaderStates.h_CON : HeaderStates.h_general);
                                        break;

                                    case HeaderStates.h_CON:
                                        index++;
                                        switch (c)
                                        {
                                            case 'n':
                                                header_state = HeaderStates.h_matching_connection;
                                                break;
                                            case 't':
                                                header_state = HeaderStates.h_matching_content_length;
                                                break;
                                            default:
                                                header_state = HeaderStates.h_general;
                                                break;
                                        }
                                        break;

                                    /* connection */

                                    case HeaderStates.h_matching_connection:
                                        index++;
                                        if (index > CONNECTION.Length - 0
                                            || c != CONNECTION[index])
                                        {
                                            header_state = HeaderStates.h_general;
                                        }
                                        else if (index == CONNECTION.Length - 1)
                                        {
                                            header_state = HeaderStates.h_connection;
                                        }
                                        break;

                                    /* proxy-connection */

                                    case HeaderStates.h_matching_proxy_connection:
                                        index++;
                                        if (index > PROXY_CONNECTION.Length - 0
                                            || c != PROXY_CONNECTION[index])
                                        {
                                            header_state = HeaderStates.h_general;
                                        }
                                        else if (index == PROXY_CONNECTION.Length - 1)
                                        {
                                            header_state = HeaderStates.h_connection;
                                        }
                                        break;

                                    /* content-length */

                                    case HeaderStates.h_matching_content_length:
                                        index++;
                                        if (index > CONTENT_LENGTH.Length - 0
                                            || c != CONTENT_LENGTH[index])
                                        {
                                            header_state = HeaderStates.h_general;
                                        }
                                        else if (index == CONTENT_LENGTH.Length - 1)
                                        {
                                            header_state = HeaderStates.h_content_length;
                                        }
                                        break;

                                    /* transfer-encoding */

                                    case HeaderStates.h_matching_transfer_encoding:
                                        index++;
                                        if (index > TRANSFER_ENCODING.Length - 0
                                            || c != TRANSFER_ENCODING[index])
                                        {
                                            header_state = HeaderStates.h_general;
                                        }
                                        else if (index == TRANSFER_ENCODING.Length - 1)
                                        {
                                            header_state = HeaderStates.h_transfer_encoding;
                                        }
                                        break;

                                    /* upgrade */

                                    case HeaderStates.h_matching_upgrade:
                                        index++;
                                        if (index > UPGRADE.Length - 0
                                            || c != UPGRADE[index])
                                        {
                                            header_state = HeaderStates.h_general;
                                        }
                                        else if (index == UPGRADE.Length - 1)
                                        {
                                            header_state = HeaderStates.h_upgrade;
                                        }
                                        break;

                                    case HeaderStates.h_connection:
                                    case HeaderStates.h_content_length:
                                    case HeaderStates.h_transfer_encoding:
                                    case HeaderStates.h_upgrade:
                                        if (ch != ' ') header_state = HeaderStates.h_general;
                                        break;

                                    default:
                                        System.Diagnostics.Debug.Assert("Unknown header_state" == null);
                                        break;
                                }
                                break;
                            }

                            if (ch == ':')
                            {
                                if (header_field_mark != null)
                                {
                                    if (_settings.on_header_field != null)
                                    {
                                        if (0 != _settings.on_header_field(this, header_field_mark, (uint)(p - header_field_mark)))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_header_field);
                                            return (uint)(p - data);
                                        }
                                    }
                                    header_field_mark = null;
                                }
                                state = State.s_header_value_start;
                                break;
                            }

                            if (ch == '\r')
                            {
                                state = State.s_header_almost_done;
                                if (header_field_mark != null)
                                {
                                    if (_settings.on_header_field != null)
                                    {
                                        if (0 != _settings.on_header_field(this, header_field_mark, (uint)(p - header_field_mark)))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_header_field);
                                            return (uint)(p - data);
                                        }
                                    }
                                    header_field_mark = null;
                                }
                                break;
                            }

                            if (ch == '\n')
                            {
                                if (header_field_mark != null)
                                {
                                    if (_settings.on_header_field != null)
                                    {
                                        if (0 != _settings.on_header_field(this, header_field_mark, (uint)(p - header_field_mark)))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_header_field);
                                            return (uint)(p - data);
                                        }
                                    }
                                    header_field_mark = null;
                                }
                                state = State.s_header_field_start;
                                break;
                            }

                            SET_ERRNO(HttpErrNo.INVALID_HEADER_TOKEN);
                            throw new ParseException();
                        }

                        case State.s_header_value_start:
                        {
                            if (ch == ' ' || ch == '\t') break;

                            header_value_mark = p;

                            state = State.s_header_value;
                            index = 0;

                            if (ch == '\r')
                            {
                                if (header_value_mark != null)
                                {
                                    if (_settings.on_header_value != null)
                                    {
                                        if (0 != _settings.on_header_value(this, header_value_mark, (uint)(p - header_value_mark)))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_header_value);
                                            return (uint)(p - data);
                                        }
                                    }
                                    header_value_mark = null;
                                }
                                header_state = HeaderStates.h_general;
                                state = State.s_header_almost_done;
                                break;
                            }

                            if (ch == '\n')
                            {
                                if (header_value_mark != null)
                                {
                                    if (_settings.on_header_value != null)
                                    {
                                        if (0 != _settings.on_header_value(this, header_value_mark, (uint)(p - header_value_mark)))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_header_value);
                                            return (uint)(p - data);
                                        }
                                    }
                                    header_value_mark = null;
                                }
                                state = State.s_header_field_start;
                                break;
                            }

                            c = LOWER(ch);

                            switch (header_state)
                            {
                                case HeaderStates.h_upgrade:
                                    _flags |= Flags.F_UPGRADE;
                                    header_state = HeaderStates.h_general;
                                    break;

                                case HeaderStates.h_transfer_encoding:
                                    /* looking for 'Transfer-Encoding: chunked' */
                                    if ('c' == c)
                                    {
                                        header_state = HeaderStates.h_matching_transfer_encoding_chunked;
                                    }
                                    else
                                    {
                                        header_state = HeaderStates.h_general;
                                    }
                                    break;

                                case HeaderStates.h_content_length:
                                    if (!IS_NUM(ch))
                                    {
                                        SET_ERRNO(HttpErrNo.INVALID_CONTENT_LENGTH);
                                        throw new ParseException();
                                    }

                                    _content_length = ch - '0';
                                    break;

                                case HeaderStates.h_connection:
                                    /* looking for 'Connection: keep-alive' */
                                    if (c == 'k')
                                    {
                                        header_state = HeaderStates.h_matching_connection_keep_alive;
                                        /* looking for 'Connection: close' */
                                    }
                                    else if (c == 'c')
                                    {
                                        header_state = HeaderStates.h_matching_connection_close;
                                    }
                                    else
                                    {
                                        header_state = HeaderStates.h_general;
                                    }
                                    break;

                                default:
                                    header_state = HeaderStates.h_general;
                                    break;
                            }
                            break;
                        }

                        case State.s_header_value:
                        {

                            if (ch == '\r')
                            {
                                if (header_value_mark != null)
                                {
                                    if (_settings.on_header_value != null)
                                    {
                                        if (0 != _settings.on_header_value(this, header_value_mark, (uint)(p - header_value_mark)))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_header_value);
                                            return (uint)(p - data);
                                        }
                                    }
                                    header_value_mark = null;
                                }
                                state = State.s_header_almost_done;
                                break;
                            }

                            if (ch == '\n')
                            {
                                if (header_value_mark != null)
                                {
                                    if (_settings.on_header_value != null)
                                    {
                                        if (0 != _settings.on_header_value(this, header_value_mark, (uint)(p - header_value_mark)))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_header_value);
                                            return (uint)(p - data);
                                        }
                                    }
                                    header_value_mark = null;
                                }
                                goto header_almost_done;
                            }

                            c = LOWER(ch);

                            switch (header_state)
                            {
                                case HeaderStates.h_general:
                                    break;

                                case HeaderStates.h_connection:
                                case HeaderStates.h_transfer_encoding:
                                    System.Diagnostics.Debug.Assert("Shouldn't get here." == null);
                                    break;

                                case HeaderStates.h_content_length:
                                    if (ch == ' ') break;
                                    if (!IS_NUM(ch))
                                    {
                                        SET_ERRNO(HttpErrNo.INVALID_CONTENT_LENGTH);
                                        throw new ParseException();
                                    }

                                    _content_length *= 10;
                                    _content_length += ch - '0';
                                    break;

                                /* Transfer-Encoding: chunked */
                                case HeaderStates.h_matching_transfer_encoding_chunked:
                                    index++;
                                    if (index > CHUNKED.Length - 0
                                        || c != CHUNKED[index])
                                    {
                                        header_state = HeaderStates.h_general;
                                    }
                                    else if (index == CHUNKED.Length - 1)
                                    {
                                        header_state = HeaderStates.h_transfer_encoding_chunked;
                                    }
                                    break;

                                /* looking for 'Connection: keep-alive' */
                                case HeaderStates.h_matching_connection_keep_alive:
                                    index++;
                                    if (index > KEEP_ALIVE.Length - 0
                                        || c != KEEP_ALIVE[index])
                                    {
                                        header_state = HeaderStates.h_general;
                                    }
                                    else if (index == KEEP_ALIVE.Length - 1)
                                    {
                                        header_state = HeaderStates.h_connection_keep_alive;
                                    }
                                    break;

                                /* looking for 'Connection: close' */
                                case HeaderStates.h_matching_connection_close:
                                    index++;
                                    if (index > CLOSE.Length - 0 || c != CLOSE[index])
                                    {
                                        header_state = HeaderStates.h_general;
                                    }
                                    else if (index == CLOSE.Length - 1)
                                    {
                                        header_state = HeaderStates.h_connection_close;
                                    }
                                    break;

                                case HeaderStates.h_transfer_encoding_chunked:
                                case HeaderStates.h_connection_keep_alive:
                                case HeaderStates.h_connection_close:
                                    if (ch != ' ') header_state = HeaderStates.h_general;
                                    break;

                                default:
                                    state = State.s_header_value;
                                    header_state = HeaderStates.h_general;
                                    break;
                            }
                            break;
                        }

                        case State.s_header_almost_done:
header_almost_done:
                        {
                            STRICT_CHECK(ch != '\n');

                            state = State.s_header_value_lws;

                            switch (header_state)
                            {
                                case HeaderStates.h_connection_keep_alive:
                                    _flags |= Flags.F_CONNECTION_KEEP_ALIVE;
                                    break;
                                case HeaderStates.h_connection_close:
                                    _flags |= Flags.F_CONNECTION_CLOSE;
                                    break;
                                case HeaderStates.h_transfer_encoding_chunked:
                                    _flags |= Flags.F_CHUNKED;
                                    break;
                                default:
                                    break;
                            }
                            break;
                        }

                        case State.s_header_value_lws:
                        {
                            if (ch == ' ' || ch == '\t')
                                state = State.s_header_value_start;
                            else
                            {
                                state = State.s_header_field_start;
                                goto header_field_start;
                            }
                            break;
                        }

                        case State.s_headers_almost_done:
headers_almost_done:
                        {
                            STRICT_CHECK(ch != '\n');

                            if ((_flags & Flags.F_TRAILING) != 0)
                            {
                                /* End of a chunked request */
                                if (_settings.on_message_complete != null) {
                                    if (0 != _settings.on_message_complete(this)) {
                                        SET_ERRNO(HttpErrNo.CB_message_complete);
                                        return (uint)(p - data);
                                    }
                                }
                                state = NEW_MESSAGE();
                                break;
                            }

                            nread = 0;

                            if ((_flags & Flags.F_UPGRADE) != 0 || (_method == HttpMethod.HTTP_CONNECT))
                            {
                                _upgrade = 1;
                            }

                            /* Here we call the headers_complete callback. This is somewhat
                             * different than other callbacks because if the user returns 1, we
                             * will interpret that as saying that this message has no body. This
                             * is needed for the annoying case of recieving a response to a HEAD
                             * request.
                             */
                            if (_settings.on_headers_complete != null)
                            {
                                switch (_settings.on_headers_complete(this))
                                {
                                    case 0:
                                        break;

                                    case 1:
                                        _flags |= Flags.F_SKIPBODY;
                                        break;

                                    default:
                                        _state = state;
                                        SET_ERRNO(HttpErrNo.CB_headers_complete);
                                        return (uint)(p - data); /* Error */
                                }
                            }

                            /* Exit, the rest of the connect is in a different protocol. */
                            if (_upgrade != 0)
                            {
                                if (_settings.on_message_complete != null)
                                {
                                    if (0 != _settings.on_message_complete(this))
                                    {
                                        SET_ERRNO(HttpErrNo.CB_message_complete);
                                        return (uint)(p - data);
                                    }
                                }
                                return (uint)((p - data) + 1);
                            }

                            if ((_flags & Flags.F_SKIPBODY) != 0)
                            {
                                if (_settings.on_message_complete != null)
                                {
                                    if (0 != _settings.on_message_complete(this))
                                    {
                                        SET_ERRNO(HttpErrNo.CB_message_complete);
                                        return (uint)(p - data);
                                    }
                                }
                                state = NEW_MESSAGE();
                            }
                            else if ((_flags & Flags.F_CHUNKED) != 0)
                            {
                                /* chunked encoding - ignore Content-Length header */
                                state = State.s_chunk_size_start;
                            }
                            else
                            {
                                if (_content_length == 0)
                                {
                                    /* Content-Length header given but zero: Content-Length: 0\r\n */
                                    if (_settings.on_message_complete != null)
                                    {
                                        if (0 != _settings.on_message_complete(this))
                                        {
                                            SET_ERRNO(HttpErrNo.CB_message_complete);
                                            return (uint)(p - data);
                                        }
                                    }
                                    state = NEW_MESSAGE();
                                }
                                else if (_content_length > 0)
                                {
                                    /* Content-Length header given and non-zero */
                                    state = State.s_body_identity;
                                }
                                else
                                {
                                    if (_type == HttpParserType.HTTP_REQUEST || http_should_keep_alive() != 0)
                                    {
                                        /* Assume content-length 0 - read the next */
                                        if (_settings.on_message_complete != null)
                                        {
                                            if (0 != _settings.on_message_complete(this))
                                            {
                                                SET_ERRNO(HttpErrNo.CB_message_complete);
                                                return (uint)(p - data);
                                            }
                                        }
                                        state = NEW_MESSAGE();
                                    }
                                    else
                                    {
                                        /* Read body until EOF */
                                        state = State.s_body_identity_eof;
                                    }
                                }
                            }

                            break;
                        }

                    case State.s_body_identity:
                        to_read = MIN(pe - p, _content_length);
                        if (to_read > 0) {
                            if (_settings.on_body != null) _settings.on_body(this, p, (uint)to_read);
                            p += to_read - 1;
                            _content_length -= to_read;
                            if (_content_length == 0) {
                                if (_settings.on_message_complete != null)
                                {
                                    if (0 != _settings.on_message_complete(this))
                                    {
                                        SET_ERRNO(HttpErrNo.CB_message_complete);
                                        return (uint)(p - data);
                                    }
                                }
                                state = NEW_MESSAGE();
                            }
                        }
                        break;

                      /* read until EOF */
                    case State.s_body_identity_eof:
                        to_read = pe - p;
                        if (to_read > 0) {
                            if (_settings.on_body != null) _settings.on_body(this, p, (uint)to_read);
                            p += to_read - 1;
                        }
                        break;

                    case State.s_chunk_size_start:
                    {
                        System.Diagnostics.Debug.Assert(nread == 1);
                        System.Diagnostics.Debug.Assert((_flags & Flags.F_CHUNKED) != 0);

                        unhex_val = LookupTables.unhex[ch];
                        if (unhex_val == -1) {
                            SET_ERRNO(HttpErrNo.INVALID_CHUNK_SIZE);
                            throw new ParseException();
                        }

                        _content_length = unhex_val;
                        state = State.s_chunk_size;
                        break;
                    }

                    case State.s_chunk_size:
                    {
                        System.Diagnostics.Debug.Assert((_flags & Flags.F_CHUNKED) != 0);

                        if (ch == '\r') {
                            state = State.s_chunk_size_almost_done;
                            break;
                        }

                        unhex_val = LookupTables.unhex[ch];

                        if (unhex_val == -1) {
                            if (ch == ';' || ch == ' ') {
                                state = State.s_chunk_parameters;
                                break;
                            }

                            SET_ERRNO(HttpErrNo.INVALID_CHUNK_SIZE);
                            throw new ParseException();
                        }

                        _content_length *= 16;
                        _content_length += unhex_val;
                        break;
                    }

                    case State.s_chunk_parameters:
                    {
                        System.Diagnostics.Debug.Assert((_flags & Flags.F_CHUNKED) != 0);
                        /* just ignore this shit. TODO check for overflow */
                        if (ch == '\r') {
                            state = State.s_chunk_size_almost_done;
                            break;
                        }
                        break;
                    }

                    case State.s_chunk_size_almost_done:
                    {
                        System.Diagnostics.Debug.Assert((_flags & Flags.F_CHUNKED) != 0);
                        STRICT_CHECK(ch != '\n');

                        nread = 0;

                        if (_content_length == 0) {
                            _flags |= Flags.F_TRAILING;
                            state = State.s_header_field_start;
                        } else {
                            state = State.s_chunk_data;
                        }
                        break;
                    }

                    case State.s_chunk_data:
                    {
                        System.Diagnostics.Debug.Assert((_flags & Flags.F_CHUNKED) != 0);

                        to_read = MIN(pe - p, _content_length);

                        if (to_read > 0) {
                            if (_settings.on_body != null) _settings.on_body(this, p, (uint)to_read);
                            p += to_read - 1;
                        }

                        if (to_read == _content_length) {
                            state = State.s_chunk_data_almost_done;
                        }

                        _content_length -= to_read;
                        break;
                    }

                    case State.s_chunk_data_almost_done:
                        System.Diagnostics.Debug.Assert((_flags & Flags.F_CHUNKED) != 0);
                        STRICT_CHECK(ch != '\r');
                        state = State.s_chunk_data_done;
                        break;

                    case State.s_chunk_data_done:
                        System.Diagnostics.Debug.Assert((_flags & Flags.F_CHUNKED) != 0);
                        STRICT_CHECK(ch != '\n');
                        state = State.s_chunk_size_start;
                        break;

                    default:
                        System.Diagnostics.Debug.Assert("unhandled state" != null);
                        SET_ERRNO(HttpErrNo.INVALID_INTERNAL_STATE);
                        throw new ParseException();
                    }
                }

                if (header_field_mark != null)
                {
                    if (_settings.on_header_field != null)
                    {
                        if (0 != _settings.on_header_field(this, header_field_mark, (uint)(p - header_field_mark))) {
                            SET_ERRNO(HttpErrNo.CB_header_field);
                            return (uint)(p - data);
                        }
                    }
                    header_field_mark = null;
                }

                if (header_value_mark != null)
                {
                    if (_settings.on_header_value != null)
                    {
                        if (0 != _settings.on_header_value(this, header_value_mark, (uint)(p - header_value_mark)))
                        {
                            SET_ERRNO(HttpErrNo.CB_header_value);
                            return (uint)(p - data);
                        }
                    }
                    header_value_mark = null;
                }

                if (url_mark != null)
                {
                    if (_settings.on_url != null)
                    {
                        if (0 != _settings.on_url(this, url_mark, (uint)(p - url_mark)))
                        {
                            SET_ERRNO(HttpErrNo.CB_url);
                            return (uint)(p - data);
                        }
                    }
                    url_mark = null;
                }

                _state = state;
                _header_state = header_state;
                _index = index;
                _nread = nread;

                return len;
            }
            catch (ParseException)
            {
                if (_http_errno == HttpErrNo.OK)
                {
                    SET_ERRNO(HttpErrNo.UNKNOWN);
                }

                return (uint)(p - data);
            }
        }

        private int http_should_keep_alive()
        {
            if (_http_major > 0 && _http_minor > 0)
            {
                /* HTTP/1.1 */
                if ((_flags & Flags.F_CONNECTION_CLOSE) != 0)
                {
                    return 0;
                }
                else
                {
                    return 1;
                }
            }
            else
            {
                /* HTTP/1.0 or earlier */
                if ((_flags & Flags.F_CONNECTION_KEEP_ALIVE) != 0)
                {
                    return 1;
                }
                else
                {
                    return 0;
                }
            }
        }

        public static string http_method_str(HttpMethod m)
        {
            return LookupTables.method_strings[(int)m];
        }

        public static string http_errno_name(HttpErrNo err) {
            return err.Name();
        }

        public static string http_errno_description(HttpErrNo err) {
            return err.Description();
        }
    }
}
