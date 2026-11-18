import Foundation

/// Notices when another program changes a file and calls `onChange` on the main queue.
///
/// Two mechanisms feed the same callback, which must be idempotent and cheap (the owner compares the file with
/// what it has loaded):
/// - a kqueue-backed `DispatchSource` on the file, for instant notification. It is re-opened after an atomic
///   replace: editors and sync clients usually write a temporary file and rename it over the original, which
///   unlinks the very inode being watched.
/// - a one-second poll, as a safety net for volumes that produce no vnode events and for the moments when the
///   file briefly does not exist (delete-then-create writers).
final class FileWatcher {
    private let url: URL
    private let onChange: () -> Void
    private var source: DispatchSourceFileSystemObject?
    private var poll: DispatchSourceTimer?
    private var settle: DispatchWorkItem?

    init(url: URL, onChange: @escaping () -> Void) {
        self.url = url
        self.onChange = onChange
        arm()

        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + 1, repeating: 1, leeway: .milliseconds(200))
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            if self.source == nil { self.arm() }
            self.onChange()
        }
        timer.resume()
        poll = timer
    }

    deinit { stop() }

    func stop() {
        settle?.cancel()
        settle = nil
        poll?.cancel()
        poll = nil
        source?.cancel()
        source = nil
    }

    /// (Re)opens the file at `url` and starts listening. Does nothing if the file does not exist right now;
    /// the poll tries again.
    private func arm() {
        source?.cancel()
        source = nil
        let fd = open(url.path, O_EVTONLY)
        guard fd >= 0 else { return }
        let s = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: fd,
            eventMask: [.write, .extend, .attrib, .delete, .rename, .revoke],
            queue: .main)
        s.setEventHandler { [weak self, weak s] in
            guard let self, let s else { return }
            // The watched inode is gone (atomic replace, rename, delete): follow the path to the new file.
            if !s.data.isDisjoint(with: [.delete, .rename, .revoke]) { self.arm() }
            self.scheduleCheck()
        }
        s.setCancelHandler { close(fd) }
        s.resume()
        source = s
    }

    /// Writers often touch a file several times in a row; wait until it has been quiet for a moment.
    /// (A writer that never pauses is still picked up by the poll.)
    private func scheduleCheck() {
        settle?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.onChange() }
        settle = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: work)
    }
}
