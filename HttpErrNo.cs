using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HttpParser
{
    public enum HttpErrNo
    {
        OK = 0,
        CB_message_begin = -1,
        CB_path = -2,
        CB_query_string = -3,
        CB_url = -4,
        CB_fragment = -5,
        CB_header_field = -6,
        CB_header_value = -7,
        CB_headers_complete = -8,
        CB_body = -9,
        CB_message_complete = -10,
        INVALID_EOF_STATE = -11,
        HEADER_OVERFLOW = -12,
        CLOSED_CONNECTION = -13,
        INVALID_VERSION = -14,
        INVALID_STATUS = -15,
        INVALID_METHOD = -16,
        INVALID_URL = -17,
        INVALID_HOST = -18,
        INVALID_PORT = -19,
        INVALID_PATH = -20,
        INVALID_QUERY_STRING = -21,
        INVALID_FRAGMENT = -22,
        LF_EXPECTED = -23,
        INVALID_HEADER_TOKEN = -24,
        INVALID_CONTENT_LENGTH = -25,
        INVALID_CHUNK_SIZE = -26,
        INVALID_CONSTANT = -27,
        INVALID_INTERNAL_STATE = -28,
        STRICT = -29,
        UNKNOWN = -30
    }

    public static class HttpErrNoExtensions
    {
        public static string Name(this HttpErrNo errno)
        {
            return Enum.GetName(typeof(HttpErrNo), errno);
        }

        public static string Description(this HttpErrNo errno)
        {
            switch (errno)
            {
                case HttpErrNo.OK: return "success";

                /* Callback-related errors */
                case HttpErrNo.CB_message_begin: return "the on_message_begin callback failed";
                case HttpErrNo.CB_path: return "the on_path callback failed";
                case HttpErrNo.CB_query_string: return "the on_query_string callback failed";
                case HttpErrNo.CB_url: return "the on_url callback failed";
                case HttpErrNo.CB_fragment: return "the on_fragment callback failed";
                case HttpErrNo.CB_header_field: return "the on_header_field callback failed";
                case HttpErrNo.CB_header_value: return "the on_header_value callback failed";
                case HttpErrNo.CB_headers_complete: return "the on_headers_complete callback failed";
                case HttpErrNo.CB_body: return "the on_body callback failed";
                case HttpErrNo.CB_message_complete: return "the on_message_complete callback failed";

                /* Parsing-related errors */
                case HttpErrNo.INVALID_EOF_STATE: return "stream ended at an unexpected time";
                case HttpErrNo.HEADER_OVERFLOW: return "too many header bytes seen; overflow detected";
                case HttpErrNo.CLOSED_CONNECTION: return "data received after completed connection: close message";
                case HttpErrNo.INVALID_VERSION: return "invalid HTTP version";
                case HttpErrNo.INVALID_STATUS: return "invalid HTTP status code";
                case HttpErrNo.INVALID_METHOD: return "invalid HTTP method";
                case HttpErrNo.INVALID_URL: return "invalid URL";
                case HttpErrNo.INVALID_HOST: return "invalid host";
                case HttpErrNo.INVALID_PORT: return "invalid port";
                case HttpErrNo.INVALID_PATH: return "invalid path";
                case HttpErrNo.INVALID_QUERY_STRING: return "invalid query string";
                case HttpErrNo.INVALID_FRAGMENT: return "invalid fragment";
                case HttpErrNo.LF_EXPECTED: return "LF character expected";
                case HttpErrNo.INVALID_HEADER_TOKEN: return "invalid character in header";
                case HttpErrNo.INVALID_CONTENT_LENGTH: return "invalid character in content-length header";
                case HttpErrNo.INVALID_CHUNK_SIZE: return "invalid character in chunk size header";
                case HttpErrNo.INVALID_CONSTANT: return "invalid constant string";
                case HttpErrNo.INVALID_INTERNAL_STATE: return "encountered unexpected internal state";
                case HttpErrNo.STRICT: return "strict mode assertion failed";
                case HttpErrNo.UNKNOWN: return "an unknown error occurred";

                default: throw new ArgumentOutOfRangeException("Unknown HttpErrNo.");
            }
        }
    }
}
