#if os(iOS) || os(macOS)
import Foundation
@preconcurrency import Network

extension HLSLoopbackRoute: URLSessionDataDelegate {
    func pump() {
        guard !invalidated, let plan else { return }
        for client in Array(clients.values) where !client.closed && client.taskID == nil {
            guard let request = client.request, let resource = client.resource else { continue }
            do {
                let policy = try HLSRequestPolicy(plan: plan, resource: resource)
                let origin = try PlaybackHTTPOrigin(policy.current)
                guard let reservation = budget.acquire(host: origin.host, bytes: budget.limits.windowBytes,
                    additionalStorageBytes: resource.kind == .playlist ? HLSPlaylist.workingBytes : 0) else { continue }
                // Playlists must be fully rewritten before serving HEAD/Range.
                // Media keeps the client's exact method and single byte range.
                let upstream = policy.request(method: resource.kind == .playlist ? "GET" : request.method,
                                              range: resource.kind == .playlist ? nil : request.range)
                let task = makeSession().dataTask(with: upstream)
                let transfer = HLSTransfer(task: task, client: client, resource: resource, request: request,
                                           reservation: reservation, policy: policy)
                client.taskID = task.taskIdentifier
                transfers[task.taskIdentifier] = transfer
                armDeadline(client, seconds: 30)
                task.resume()
            } catch { fail(error as? PlaybackNetworkError ?? .invalidResponse); return }
        }
    }

    func appendOutput(_ data: Data, to transfer: HLSTransfer) throws {
        guard !invalidated, !transfer.cancelled, !transfer.client.closed else { return }
        let maximum = transfer.resource.kind == .playlist
            ? HLSPlaylist.maximumOutputBytes + 2048 : budget.limits.windowBytes / 2
        guard data.count <= maximum - transfer.buffered else { throw PlaybackNetworkError.resourceBudgetExceeded }
        transfer.buffered += data.count; transfer.output.append(data)
        sendNext(transfer)
    }

    func sendNext(_ transfer: HLSTransfer) {
        guard !invalidated, !transfer.cancelled, !transfer.client.closed, !transfer.sending else { return }
        let client = transfer.client
        if !transfer.output.isEmpty {
            let data = transfer.output.removeFirst()
            transfer.sending = true
            client.connection.send(content: data, completion: .contentProcessed { [self, transfer, data] error in
                transfer.sending = false
                transfer.buffered -= data.count
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
            do { try appendOutput(Data("0\r\n\r\n".utf8), to: transfer) }
            catch { fail(.resourceBudgetExceeded) }
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
        transfer.output.removeAll(); transfer.playlist.removeAll(); transfer.policy.revoke()
        transfers[transfer.task.taskIdentifier] = nil
        // Both the actual URLSession completion and submitted Network payload
        // completion have occurred. Retirement alone never reaches this call.
        transfer.reservation.complete()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        guard !invalidated, let transfer = transfers[dataTask.taskIdentifier], !transfer.client.closed else {
            completionHandler(.cancel); return
        }
        do {
            guard let http = response as? HTTPURLResponse, let url = response.url,
                  url.absoluteString == transfer.policy.current.absoluteString else {
                throw PlaybackNetworkError.redirectDenied
            }
            let parsed = try HLSUpstreamResponse.parse(http, kind: transfer.resource.kind, request: transfer.request)
            transfer.response = parsed
            if transfer.resource.kind == .media {
                guard !PlaybackNetworkPlan.hlsExtensions.contains(url.pathExtension.lowercased()) else {
                    throw PlaybackNetworkError.hlsUnsupportedFeature
                }
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
            try appendOutput(parsed.head(chunked: transfer.chunked), to: transfer)
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
            let maximum: Int64 = transfer.resource.kind == .playlist ? Int64(HLSPlaylist.maximumInputBytes) :
                transfer.resource.kind == .media ? 512 * 1024 * 1024 : 64 * 1024
            guard Int64(data.count) <= maximum - transfer.received,
                  response.length == nil || Int64(data.count) <= response.length! - transfer.received else {
                throw PlaybackNetworkError.resourceBudgetExceeded
            }
            transfer.received += Int64(data.count)
            if transfer.resource.kind == .playlist {
                transfer.playlist.append(data)
            } else {
                // Validate BEFORE framing/slicing/copying a delegate chunk.
                // Foundation's own receive storage is outside this counter.
                let available = budget.limits.windowBytes / 2 - transfer.buffered - transfer.prefix.count
                guard data.count <= available - (transfer.chunked ? 64 : 0) else {
                    throw PlaybackNetworkError.resourceBudgetExceeded
                }
                var offset = 0
                if !transfer.checkedPrefix {
                    let count = min(16 - transfer.prefix.count, data.count)
                    transfer.prefix.append(data.prefix(count)); offset = count
                    if transfer.prefix.count == 16 || transfer.received == response.length {
                        try deliverPrefix(transfer)
                    }
                }
                if offset < data.count { try deliverStream(Data(data.dropFirst(offset)), transfer: transfer) }
            }
            armDeadline(transfer.client, seconds: 30)
        } catch { fail(error as? PlaybackNetworkError ?? .invalidResponse) }
    }

    private func deliverPrefix(_ transfer: HLSTransfer) throws {
        guard !ProgressiveMediaResponse.isPlaylistPrefix(transfer.prefix) else {
            throw PlaybackNetworkError.hlsUnsupportedFeature
        }
        transfer.checkedPrefix = true
        let bytes = transfer.prefix; transfer.prefix.removeAll()
        if !bytes.isEmpty { try deliverStream(bytes, transfer: transfer) }
    }

    private func deliverStream(_ data: Data, transfer: HLSTransfer) throws {
        // Explicit DataTask, not AsyncBytes. Suspend at the first queued chunk
        // and resume only when its actual socket-send backlog is empty.
        if !transfer.completed, !transfer.suspended { transfer.task.suspend(); transfer.suspended = true }
        if transfer.chunked {
            var chunk = Data((String(data.count, radix: 16) + "\r\n").utf8)
            chunk.append(data); chunk.append(Data("\r\n".utf8))
            try appendOutput(chunk, to: transfer)
        } else { try appendOutput(data, to: transfer) }
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
                    inheritsHeaders: transfer.policy.permitsHeaders, registry: registry, playlistID: transfer.resource.id)
                transfer.playlist.removeAll()
                try servePlaylist(bytes, transfer: transfer)
            } else if !transfer.checkedPrefix {
                try deliverPrefix(transfer)
            }
            sendNext(transfer)
        } catch { fail(error as? PlaybackNetworkError ?? .invalidResponse) }
    }

    func servePlaylist(_ bytes: Data, transfer: HLSTransfer) throws {
        let length = Int64(bytes.count)
        let range = transfer.request.range?.resolved(total: length)
        let unsatisfied = transfer.request.range != nil && range == nil
        let status = unsatisfied ? 416 : range == nil ? 200 : 206
        let size = unsatisfied ? 0 : range.map { $0.upperBound - $0.lowerBound + 1 } ?? length
        let contentRange = unsatisfied ? "bytes */\(length)" :
            range.map { "bytes \($0.lowerBound)-\($0.upperBound)/\(length)" }
        let response = HLSUpstreamResponse(status: status, length: size,
            mime: "application/vnd.apple.mpegurl", contentRange: contentRange, discardBody: transfer.request.method == "HEAD")
        // Enqueue atomically before the first send: a completion must not see
        // a temporarily empty queue and end the HTTP message prematurely.
        var output = [response.head(chunked: false)]
        if transfer.request.method != "HEAD", !unsatisfied {
            var index = Int(range?.lowerBound ?? 0)
            let end = index + Int(size)
            while index < end {
                let count = min(budget.limits.deliveryBytes, end - index)
                output.append(bytes.subdata(in: index..<(index + count))); index += count
            }
        }
        transfer.buffered = output.reduce(0) { $0 + $1.count }
        transfer.output = output
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        guard !invalidated, let transfer = transfers[task.taskIdentifier], !transfer.client.closed else {
            completionHandler(nil); return
        }
        do {
            guard [301, 302, 303, 307, 308].contains(response.statusCode), let target = request.url else {
                throw PlaybackNetworkError.redirectDenied
            }
            try transfer.policy.redirect(to: target)
            let host = try PlaybackHTTPOrigin(transfer.policy.current).host
            guard transfer.reservation.move(to: host) else { throw PlaybackNetworkError.resourceBudgetExceeded }
            completionHandler(transfer.policy.request(
                method: transfer.resource.kind == .playlist ? "GET" : transfer.request.method,
                range: transfer.resource.kind == .playlist ? nil : transfer.request.range))
        } catch {
            completionHandler(nil); fail(error as? PlaybackNetworkError ?? .redirectDenied)
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        let allowed = !invalidated && transfers[task.taskIdentifier]?.client.closed == false
        completeChallenge(challenge, allowed: allowed, completionHandler: completionHandler)
    }
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        completeChallenge(challenge, allowed: !invalidated, completionHandler: completionHandler)
    }
    private func completeChallenge(_ challenge: URLAuthenticationChallenge, allowed: Bool,
        completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        completionHandler(allowed && challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust
                          ? .performDefaultHandling : .cancelAuthenticationChallenge, nil)
    }
    func urlSession(_ session: URLSession, didBecomeInvalidWithError error: Error?) { self.session = nil }
}
#endif
