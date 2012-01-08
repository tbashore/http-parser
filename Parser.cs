using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HttpParser
{
    public class Parser
    {
        public delegate void MessageBeginEventHandler(Parser parser);
        public event MessageBeginEventHandler MessageBegin;

        public delegate void MessageCompleteEventHandler(Parser parser);
        public event MessageCompleteEventHandler MessageComplete;

        public delegate void UrlEventHandler(Parser parser, string url);
        public event UrlEventHandler Url;

        public delegate void HeaderFieldEventHandler(Parser parser, string headerField);
        public event HeaderFieldEventHandler HeaderField;

        public delegate void HeaderValueEventHandler(Parser parser, string headerValue);
        public event HeaderValueEventHandler HeaderValue;

        public delegate void HeadersCompleteEventHandler(Parser parser);
        public event HeadersCompleteEventHandler HeadersComplete;

        public delegate void BodyEventHandler(Parser parser, string body);
        public event BodyEventHandler Body;

        private HttpParser _parser;


        public Parser(HttpParserType type)
        {
            unsafe
            {
                HttpParserSettings settings = new HttpParserSettings();
                settings.on_message_begin = OnMessageBegin;
                settings.on_message_complete = OnMessageComplete;
                settings.on_url = OnUrl;
                settings.on_header_field = OnHeaderField;
                settings.on_header_value = OnHeaderValue;
                settings.on_headers_complete = OnHeadersComplete;
                settings.on_body = OnBody;

                _parser = new HttpParser(settings, type);
            }
        }

        public uint Parse(string input)
        {
            unsafe
            {
                fixed (char* p = input)
                {
                    uint result = _parser.Execute(p, (uint)input.Length);
                    return result;
                }
            }
        }

        public HttpMethod Method
        {
            get { return _parser.Method; }
        }

        public HttpErrNo ErrNo
        {
            get { return _parser.ErrNo; }
        }

        int OnMessageBegin(HttpParser parser)
        {
            if (MessageBegin != null)
            {
                MessageBegin(this);
            }
            return 0;
        }

        unsafe int OnUrl(HttpParser parser, char* at, uint length)
        {
            if (Url != null)
            {
                string url = Marshal.PtrToStringUni((IntPtr)at, (int)length);
                Url(this, url);
            }
            return 0;
        }

        unsafe int OnHeaderField(HttpParser parser, char* at, uint length)
        {
            if (HeaderField != null)
            {
                string headerField = Marshal.PtrToStringUni((IntPtr)at, (int)length);
                HeaderField(this, headerField);
            }
            return 0;
        }

        unsafe int OnHeaderValue(HttpParser parser, char* at, uint length)
        {
            if (HeaderValue != null)
            {
                string headerValue = Marshal.PtrToStringUni((IntPtr)at, (int)length);
                HeaderValue(this, headerValue);
            }
            return 0;
        }

        int OnHeadersComplete(HttpParser parser)
        {
            if (HeadersComplete != null)
            {
                HeadersComplete(this);
            }
            return 0;
        }

        unsafe int OnBody(HttpParser parser, char* at, uint length)
        {
            if (Body != null)
            {
                string body = Marshal.PtrToStringUni((IntPtr)at, (int)length);
                Body(this, body);
            }
            return 0;
        }

        int OnMessageComplete(HttpParser parser)
        {
            if (MessageComplete != null)
            {
                MessageComplete(this);
            }
            return 0;
        }
    }
}
