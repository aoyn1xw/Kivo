import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/testing.dart';
import 'package:kivo/core/models.dart';
import 'package:kivo/core/shadowplay_api.dart';

void main() {
  late Directory temporary;
  late HttpServer server;
  late String pin;
  late Connection saved;
  final paths = <String>[];
  final clip = Clip(
      id: 'clip',
      fileName: 'clip.mp4',
      sizeBytes: 3,
      lastWriteTimeUtc: DateTime.utc(2026));

  setUpAll(() async {
    // Generate a disposable identity; never store a test private key in source.
    temporary = await Directory.systemTemp.createTemp('kivo-tls-test-');
    final openssl = Platform.isWindows &&
            File('C:/Program Files/Git/usr/bin/openssl.exe').existsSync()
        ? 'C:/Program Files/Git/usr/bin/openssl.exe'
        : 'openssl';
    final generated = await Process.run(openssl, [
      'req',
      '-x509',
      '-newkey',
      'rsa:2048',
      '-nodes',
      '-days',
      '1',
      '-subj',
      '/CN=localhost',
      '-keyout',
      '${temporary.path}/key.pem',
      '-out',
      '${temporary.path}/cert.pem',
    ], environment: {
      'MSYS_NO_PATHCONV': '1'
    });
    expect(generated.exitCode, 0, reason: generated.stderr.toString());
    final pem = await File('${temporary.path}/cert.pem').readAsString();
    final der =
        base64Decode(pem.split('-----')[2].replaceAll(RegExp(r'\s'), ''));
    pin = sha256.convert(der).toString();
    final context = SecurityContext()
      ..useCertificateChain('${temporary.path}/cert.pem')
      ..usePrivateKey('${temporary.path}/key.pem');
    server =
        await HttpServer.bindSecure(InternetAddress.loopbackIPv4, 0, context);
    server.listen((request) async {
      paths.add(request.uri.path);
      if (request.uri.path.endsWith('/health')) {
        request.response.write('{"status":"ok","apiVersion":1}');
      } else if (request.uri.path.endsWith('/pair/exchange')) {
        request.response.write(
            '{"token":"test-token","server":{"serverId":"pc","computerName":"PC"}}');
      } else if (request.uri.path.endsWith('/server')) {
        expect(request.headers.value('Authorization'), 'Bearer test-token');
        request.response.write('{"serverId":"pc","computerName":"PC"}');
      } else if (request.uri.path.endsWith('/clips')) {
        request.response.write('[]');
      } else {
        expect(request.headers.value('Range'), 'bytes=1-');
        request.response.statusCode = 206;
        request.response.headers.set('content-range', 'bytes 1-2/3');
        request.response.add([2, 3]);
      }
      await request.response.close();
    });
    saved = Connection.fromJson(Connection(
            serverId: 'pc',
            computerName: 'PC',
            address: '127.0.0.1',
            port: 2,
            httpsPort: server.port,
            certSha256: pin)
        .toJson());
  });

  tearDownAll(() async {
    await server.close(force: true);
    await temporary.delete(recursive: true);
  });

  test('QR pairing and saved reconnection use the same pinned transport',
      () async {
    final response = await ShadowPlayApi.pair(
        address: saved.address,
        port: server.port,
        code: 'test-code',
        deviceName: 'Phone',
        useTls: true,
        pinnedCertSha256: pin);
    expect(response['token'], 'test-token');
    final api = ShadowPlayApi(saved, 'test-token');
    expect((await api.serverInfo()).serverId, 'pc');
    expect(await api.clips(), isEmpty);
    expect(
        paths,
        containsAll(
            ['/api/v1/pair/exchange', '/api/v1/server', '/api/v1/clips']));
    expect(saved.port, 2);
  });

  test('range download sends through the pinned client', () async {
    final segment = await ShadowPlayApi(saved, 'test-token')
        .openDownload(clip, rangeStart: 1);
    try {
      expect(segment.statusCode, 206);
      expect(await segment.body.expand((bytes) => bytes).toList(), [2, 3]);
    } finally {
      await segment.dispose();
    }
  });

  test('actual different certificate is rejected without changing saved pin',
      () async {
    final wrong =
        Connection.fromJson(saved.toJson()..['certSha256'] = '00' * 32);
    await expectLater(
        ShadowPlayApi(wrong, 'test-token').serverInfo(),
        throwsA(isA<ApiException>()
            .having((e) => e.kind, 'kind', ApiFailureKind.tlsUntrusted)));
    expect(wrong.certSha256, '00' * 32);
  });

  test('interrupted handshake is a network failure, not a certificate change',
      () async {
    final client = MockClient((_) async => throw const HandshakeException(
        'Connection terminated during handshake'));
    try {
      await expectLater(
          ShadowPlayApi(saved, 'test-token').serverInfo(client: client),
          throwsA(isA<ApiException>()
              .having((e) => e.kind, 'kind', ApiFailureKind.network)));
    } finally {
      client.close();
    }
  });

  test('permission-denied socket is not a certificate change', () async {
    final client = MockClient((_) async => throw const SocketException(
        'Operation not permitted',
        osError: OSError('Permission denied', 1)));
    try {
      await expectLater(
        ShadowPlayApi(saved, 'test-token').serverInfo(client: client),
        throwsA(isA<ApiException>()
            .having((e) => e.kind, 'kind', ApiFailureKind.network)),
      );
    } finally {
      client.close();
    }
  });

  test('refused socket is not a certificate change', () async {
    final client = MockClient(
        (_) async => throw const SocketException('Connection refused'));
    await expectLater(
        ShadowPlayApi(saved, 'test-token').clips(client: client),
        throwsA(isA<ApiException>()
            .having((e) => e.kind, 'kind', ApiFailureKind.connectionRefused)));
    client.close();
  });
}
