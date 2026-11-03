import 'package:flutter/material.dart';

import '../core/models.dart';

class EmptyState extends StatelessWidget {
  const EmptyState({
    required this.icon,
    required this.title,
    required this.message,
    this.action,
    super.key,
  });

  final IconData icon;
  final String title;
  final String message;
  final Widget? action;

  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 32, vertical: 48),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(icon,
                  size: 36,
                  color: Theme.of(context).colorScheme.onSurfaceVariant),
              const SizedBox(height: 16),
              Text(title,
                  style: Theme.of(context).textTheme.titleLarge,
                  textAlign: TextAlign.center),
              const SizedBox(height: 8),
              Text(
                message,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: Theme.of(context).colorScheme.onSurfaceVariant,
                    ),
                textAlign: TextAlign.center,
              ),
              if (action != null) ...[const SizedBox(height: 20), action!],
            ],
          ),
        ),
      );
}

class DeviceHeader extends StatelessWidget {
  const DeviceHeader({
    required this.connection,
    required this.status,
    this.lastSyncUtc,
    this.title,
    super.key,
  });

  final Connection connection;
  final DeviceStatus status;
  final DateTime? lastSyncUtc;
  final String? title;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    final online = status.availability == DeviceAvailability.online;
    final checking = status.availability == DeviceAvailability.checking;
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 18, 20, 14),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (title != null) ...[
            Text(
              title!,
              style: Theme.of(context).textTheme.titleLarge?.copyWith(
                    fontWeight: FontWeight.w600,
                  ),
            ),
            const SizedBox(height: 20),
          ],
          const SizedBox(height: 2),
          Text(
            connection.computerName,
            style: Theme.of(context).textTheme.titleSmall,
          ),
          const SizedBox(height: 7),
          Row(
            children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: checking
                      ? colors.outline
                      : online
                          ? colors.primary
                          : colors.error,
                ),
              ),
              const SizedBox(width: 7),
              Expanded(
                child: Text(
                  checking
                      ? 'Checking connection…'
                      : online
                          ? _syncText(lastSyncUtc ?? connection.lastSeenUtc)
                          : '${status.label} · ${_relativeTime(connection.lastSeenUtc, prefix: 'last seen ')}',
                  style: Theme.of(context).textTheme.bodySmall?.copyWith(
                        color: colors.onSurfaceVariant,
                      ),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

/// Keeps recovery visible even when a cached PC library is still displayed.
class ConnectionProblemBanner extends StatelessWidget {
  const ConnectionProblemBanner(
      {required this.status,
      required this.onRetry,
      this.onPairPc,
      this.lastSyncUtc,
      this.hasCachedClips = false,
      super.key});
  final DeviceStatus status;
  final VoidCallback onRetry;
  final VoidCallback? onPairPc;
  final DateTime? lastSyncUtc;
  final bool hasCachedClips;

  Future<void> _verifyPc(BuildContext context) async {
    final confirmed = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
              title: const Text('Verify your PC first'),
              content: const Text(
                  'The saved certificate does not match. Verify that Kivo is running on your own PC, then scan the QR code displayed there. Your saved identity will not change unless you explicitly complete pairing.'),
              actions: [
                TextButton(
                    onPressed: () => Navigator.pop(context, false),
                    child: const Text('Cancel')),
                FilledButton(
                    onPressed: () => Navigator.pop(context, true),
                    child: const Text('Scan PC QR code')),
              ],
            ));
    if (confirmed == true && context.mounted) onPairPc?.call();
  }

  void _updateInstructions(BuildContext context) => showDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
            title: const Text('Update Kivo'),
            content: const Text(
                'Install a compatible Kivo mobile version from the release source you used to install the app, then try again. Your downloaded clips remain available on this phone.'),
            actions: [
              TextButton(
                  onPressed: () => Navigator.pop(context),
                  child: const Text('Close'))
            ],
          ));

  @override
  Widget build(BuildContext context) {
    if (status.issue == null) return const SizedBox.shrink();
    final (label, action) = switch (status.issue) {
      ConnectionIssue.authenticationRequired => ('Re-pair', onPairPc),
      ConnectionIssue.certificateChanged => (
          'Verify and re-pair',
          () => _verifyPc(context)
        ),
      ConnectionIssue.updateRequired => (
          'Update instructions',
          () => _updateInstructions(context)
        ),
      _ => ('Try Again', onRetry),
    };
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 0, 20, 12),
      child: Material(
        color: Theme.of(context).colorScheme.errorContainer,
        borderRadius: BorderRadius.circular(8),
        child: Padding(
            padding: const EdgeInsets.all(14),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(status.label,
                    style: Theme.of(context).textTheme.titleSmall),
                const SizedBox(height: 6),
                Text(
                    status.message ?? 'Check the PC connection and try again.'),
                if (hasCachedClips) ...[
                  const SizedBox(height: 6),
                  Text(
                      'Showing cached PC recordings · ${_relativeTime(lastSyncUtc, prefix: "last successful refresh ")}'),
                ],
                const SizedBox(height: 8),
                FilledButton.tonal(onPressed: action, child: Text(label)),
              ],
            )),
      ),
    );
  }
}

class ClipPoster extends StatelessWidget {
  const ClipPoster(
      {required this.badge,
      this.selected = false,
      this.showIcon = true,
      super.key});

  final String badge;
  final bool selected;
  final bool showIcon;

  @override
  Widget build(BuildContext context) {
    final colors = Theme.of(context).colorScheme;
    return Stack(
      fit: StackFit.expand,
      children: [
        ColoredBox(
          color: colors.surfaceContainerHigh,
          child: showIcon
              ? Icon(Icons.videocam_outlined,
                  size: 28, color: colors.onSurfaceVariant)
              : null,
        ),
        if (selected) ColoredBox(color: colors.primary.withValues(alpha: 0.13)),
        Positioned(
          right: 8,
          bottom: 8,
          child: DecoratedBox(
            decoration: BoxDecoration(
              color: Colors.black.withValues(alpha: 0.72),
              borderRadius: BorderRadius.circular(6),
            ),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 3),
              child: Text(
                badge,
                style: const TextStyle(
                    color: Colors.white,
                    fontSize: 11,
                    fontWeight: FontWeight.w600),
              ),
            ),
          ),
        ),
        if (selected)
          Positioned(
            right: 8,
            top: 8,
            child: DecoratedBox(
              decoration:
                  BoxDecoration(color: colors.primary, shape: BoxShape.circle),
              child: const Padding(
                padding: EdgeInsets.all(4),
                child: Icon(Icons.check, color: Colors.white, size: 16),
              ),
            ),
          ),
      ],
    );
  }
}

String formatBytes(int value) {
  if (value < 1000) return '$value B';
  if (value < 1000000) return '${(value / 1000).toStringAsFixed(0)} KB';
  if (value < 1000000000) return '${(value / 1000000).toStringAsFixed(1)} MB';
  return '${(value / 1000000000).toStringAsFixed(1)} GB';
}

String formatClipDate(DateTime value) {
  final local = value.toLocal();
  final now = DateTime.now();
  final sameDay = local.year == now.year &&
      local.month == now.month &&
      local.day == now.day;
  final minute = local.minute.toString().padLeft(2, '0');
  if (sameDay) return 'Today · ${local.hour}:$minute';
  return '${local.day}.${local.month}.${local.year} · ${local.hour}:$minute';
}

String formatDuration(Duration value) {
  final minutes = value.inMinutes;
  final seconds = value.inSeconds.remainder(60).toString().padLeft(2, '0');
  return '$minutes:$seconds';
}

String _syncText(DateTime? value) => value == null
    ? 'Connected · not synced yet'
    : 'Connected · ${_relativeTime(value, prefix: 'last sync ')}';

String _relativeTime(DateTime? value, {required String prefix}) {
  if (value == null) return '${prefix}unknown';
  final difference = DateTime.now().toUtc().difference(value.toUtc());
  if (difference.inMinutes < 1) return '${prefix}just now';
  if (difference.inHours < 1) return '$prefix${difference.inMinutes} min ago';
  if (difference.inDays < 1) return '$prefix${difference.inHours} hr ago';
  return '$prefix${difference.inDays} d ago';
}
