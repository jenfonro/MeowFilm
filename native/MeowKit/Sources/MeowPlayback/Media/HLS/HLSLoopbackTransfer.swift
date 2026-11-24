#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import Network

extension HLSLoopbackRoute: URLSessionDataDelegate {
    private static let deliveryBytes = 64 * 1024

    func pump() {
        guard !invalidated, let plan else { return }
        for client in Array(clients.values) where !client.closed && client.taskID == nil {
            guard let request = client.request, let resource = client.resource else { continue }
            do {
                let policy = try HLSRequestPolicy(plan: plan, resource: resource)
                let upstream = policy.request(method: resource.kind == .playlist ? "GET" : request.method,
                                              range: resource.kind == .playlist ? nil : request.range)
                let task = makeSession().dataTask(with: upstream)
                let transfer = HLSTransfer(task: task, client: client, resource: resource, request: request,
                                           policy: policy)
                client.taskID = task.taskIdentifier
                transfers[task.taskIdentifier] = transfer
                armDeadline(client, seconds: 30)
                task.resume()
            } catch { fail(error as? PlaybackNetworkError ?? .invalidResponse); return }
        }
    }

    func appendOutput(_ data: Data, to transfer: HLSTransfer) {
        guard !invalidated, !transfer.cancelled, !transfer.client.closed else { return }
        transfer.output.append(data)
        sendNext(transfer)
    }

    func sendNext(_ transfer: HLSTransfer) {
        guard !invalidated, !transfer.cancelled, !transfer.client.closed, !transfer.sending else { return }
        let client = transfer.client
        if !transfer.output.isEmpty {
            let data = transfer.output.removeFirst()
            transfer.sending = true
            client.connection.send(content: data, completion: .contentProcessed { [self, transfer] error in
                transfer.sending = false
                if error != nil { close(client) }
                if client.closed || invalidated || transfer.cancelled { releaseIfDrained(transfer); return }
                armDeadline(client, seconds: 30)
                sendNext(transfer)
            })
            return
        }
        if transfer.suspended, !transfer.completed {
            transfer.suspended = false; transfer.task.resume()
        }
        guard transfer.completed, !transfer.finishing else { return }
        if transfer.chunked, !transfer.trailerSent {
            transfer.trailerSent = true
            appendOutput(Data("0\r\n\r\n".utf8), to: transfer)
            return
        }
        transfer.finishing = true; transfer.sending = true
        client.connection.send(content: nil, contentContext: .finalMessage, isComplete: true,
                               completion: .contentProcessed { [self, transfer] error in
            transfer.sending = false
            close(transfer.client, graceful: error == nil && !invalidated && !transfer.cancelled)
            releaseIfDrained(transfer)
        })
    }

    func releaseIfDrained(_ transfer: HLSTransfer) {
        guard transfer.completed, transfer.client.closed, !transfer.sending,
              transfers[transfer.task.taskIdentifier] === transfer else { return }
        transfer.output.removeAll(); transfer.playlist.removeAll()
        transfers[transfer.task.taskIdentifier] = nil
        finishCloseIfDrained()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        guard !invalidated, let transfer = transfers[dataTask.taskIdentifier], !transfer.client.closed else {
            completionHandler(.cancel); return
        }
        do {
            guard let http = response as? HTTPURLResponse else {
                throw PlaybackNetworkError.invalidResponse
            }
            let parsed = try HLSUpstreamResponse.parse(http, kind: transfer.resource.kind, request: transfer.request)
            transfer.response = parsed
            if transfer.resource.kind == .media {
                switch transfer.request.range {
                case .closed(let start, _), .from(let start): transfer.checkedPrefix = start != 0
                case .suffix: transfer.checkedPrefix = true
                case nil: break
                }
            } else { transfer.checkedPrefix = true }
            if transfer.resource.kind == .playlist {
                completionHandler(.allow); return
            }
            transfer.chunked = parsed.length == nil && !parsed.discardBody
            appendOutput(parsed.head(chunked: transfer.chunked), to: transfer)
            if parsed.discardBody {
                transfer.expectedCancellation = true
                completionHandler(.cancel)
            } else { completionHandler(.allow) }
        } catch {
            completionHandler(.cancel)
            fail(error as? PlaybackNetworkError ?? .invalidResponse)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard !invalidated, let transfer = transfers[dataTask.taskIdentifier],
              !transfer.client.closed, !data.isEmpty else { return }
        do {
            guard let response = transfer.response, !response.discardBody else { throw PlaybackNetworkError.invalidResponse }
            let (received, overflow) = transfer.received.addingReportingOverflow(Int64(data.count))
            guard !overflow, response.length == nil || received <= response.length! else {
                throw PlaybackNetworkError.invalidResponse
            }
            transfer.received = received
            if transfer.resource.kind == .playlist {
                transfer.playlist.append(data)
            } else {
                var offset = 0
                if !transfer.checkedPrefix {
                    let count = min(16 - transfer.prefix.count, data.count)
                    transfer.prefix.append(data.prefix(count)); offset = count
                    if transfer.prefix.count == 16 || transfer.received == response.length {
                        try deliverPrefix(transfer)
                    }
                }
                if offset < data.count { deliverStream(Data(data.dropFirst(offset)), transfer: transfer) }
            }
            armDeadline(transfer.client, seconds: 30)
        } catch { fail(error as? PlaybackNetworkError ?? .invalidResponse) }
    }

    private func deliverPrefix(_ transfer: HLSTransfer) throws {
        guard !PlaybackNetworkPlan.isPlaylistPrefix(transfer.prefix) else {
            throw PlaybackNetworkError.hlsUnsupportedFeature
        }
        transfer.checkedPrefix = true
        let bytes = transfer.prefix; transfer.prefix.removeAll()
        if !bytes.isEmpty { deliverStream(bytes, transfer: transfer) }
    }

    private func deliverStream(_ data: Data, transfer: HLSTransfer) {
        if !transfer.completed, !transfer.suspended { transfer.task.suspend(); transfer.suspended = true }
        if transfer.chunked {
            var chunk = Data((String(data.count, radix: 16) + "\r\n").utf8)
            chunk.append(data); chunk.append(Data("\r\n".utf8))
            appendOutput(chunk, to: transfer)
        } else { appendOutput(data, to: transfer) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let transfer = transfers[task.taskIdentifier] else { return }
        transfer.completed = true; transfer.suspended = false
        if invalidated || transfer.cancelled || transfer.client.closed { releaseIfDrained(transfer); return }
        do {
            guard error == nil || transfer.expectedCancellation else { throw PlaybackNetworkError.transportFailed }
            guard let response = transfer.response,
                  response.discardBody || response.length == nil || response.length == transfer.received else {
                throw PlaybackNetworkError.invalidResponse
            }
            if transfer.resource.kind == .playlist {
                let bytes = try HLSPlaylist.rewrite(transfer.playlist, base: transfer.policy.current,
                    registry: registry, playlistID: transfer.resource.id)
                transfer.playlist.removeAll()
                servePlaylist(bytes, transfer: transfer)
            } else if !transfer.checkedPrefix {
                try deliverPrefix(transfer)
            }
            sendNext(transfer)
        } catch { fail(error as? PlaybackNetworkError ?? .invalidResponse) }
    }

    func servePlaylist(_ bytes: Data, transfer: HLSTransfer) {
        let length = Int64(bytes.count)
        let range = transfer.request.range?.resolved(total: length)
        let unsatisfied = transfer.request.range != nil && range == nil
        let status = unsatisfied ? 416 : range == nil ? 200 : 206
        let size = unsatisfied ? 0 : range.map { $0.upperBound - $0.lowerBound + 1 } ?? length
        let contentRange = unsatisfied ? "bytes */\(length)" :
            range.map { "bytes \($0.lowerBound)-\($0.upperBound)/\(length)" }
        let response = HLSUpstreamResponse(status: status, length: size,
            mime: "application/vnd.apple.mpegurl", contentRange: contentRange, discardBody: transfer.request.method == "HEAD")
        var output = [response.head(chunked: false)]
        if transfer.request.method != "HEAD", !unsatisfied {
            var index = Int(range?.lowerBound ?? 0)
            let end = index + Int(size)
            while index < end {
                let count = min(Self.deliveryBytes, end - index)
                output.append(bytes.subdata(in: index..<(index + count))); index += count
            }
        }
        transfer.output = output
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        guard !invalidated, let transfer = transfers[task.taskIdentifier], !transfer.client.closed else {
            completionHandler(nil); return
        }
        do {
            guard let target = request.url else { throw PlaybackNetworkError.invalidURL }
            try transfer.policy.redirect(to: target)
            completionHandler(transfer.policy.request(
                method: transfer.resource.kind == .playlist ? "GET" : transfer.request.method,
                range: transfer.resource.kind == .playlist ? nil : transfer.request.range))
        } catch {
            completionHandler(nil); fail(error as? PlaybackNetworkError ?? .invalidResponse)
        }
    }

    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) { self.session = nil }
}
#endif
