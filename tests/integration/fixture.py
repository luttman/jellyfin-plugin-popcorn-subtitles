"""Deterministic HTTPS subtitle provider; no third-party subtitle calls in CI."""
import http.server
import json
import ssl
from urllib.parse import urlparse

requests = []


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        path = urlparse(self.path).path
        requests.append(self.path)
        if path == '/requests':
            data = json.dumps(requests).encode()
            content_type = 'application/json'
        elif '/subtitles/' in path:
            base = 'https://popcorn-fixture:8443/' + path.split('/')[1] + '/'
            data = json.dumps({'subtitles': [
                {'id': lang, 'lang': lang, 'label': 'WEB-DL · TEST', 'url': base + 'track/' + lang + '.vtt'}
                for lang in ['eng', 'deu', 'fra']
            ]}).encode()
            content_type = 'application/json'
        elif '/track/' in path:
            data = b'WEBVTT\n\n00:00:00.000 --> 00:00:03.000\nCI subtitle from the selected track\n'
            content_type = 'text/vtt'
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)


server = http.server.ThreadingHTTPServer(('0.0.0.0', 8443), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain('/fixture/cert.pem', '/fixture/key.pem')
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
