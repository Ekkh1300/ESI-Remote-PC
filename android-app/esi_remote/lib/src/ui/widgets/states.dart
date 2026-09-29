import 'package:flutter/material.dart';

import '../theme.dart';

/// A screen with nothing to show yet.
///
/// An empty state is an invitation, not an apology: it says what would be here and offers
/// the one action that would put it there.
class EsiEmptyState extends StatelessWidget {
  const EsiEmptyState({
    super.key,
    required this.title,
    required this.body,
    this.icon = Icons.inbox_outlined,
    this.action,
    this.accent = EsiPalette.signal,
  });

  final String title;
  final String body;
  final IconData icon;
  final Widget? action;
  final Color accent;

  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.all(EsiSpace.xl),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Container(
                width: 54,
                height: 54,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: accent.withValues(alpha: 0.1),
                  border: Border.all(color: accent.withValues(alpha: 0.3)),
                ),
                child: Icon(icon, size: 24, color: accent),
              ),
              const SizedBox(height: EsiSpace.lg),
              Text(
                title,
                style: EsiType.subtitle,
                textAlign: TextAlign.center,
              ),
              const SizedBox(height: EsiSpace.sm),
              ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 380),
                child: Text(
                  body,
                  style: EsiType.body.copyWith(color: EsiPalette.dim),
                  textAlign: TextAlign.center,
                ),
              ),
              if (action != null) ...<Widget>[
                const SizedBox(height: EsiSpace.xl),
                action!,
              ],
            ],
          ),
        ),
      );
}

/// A failure, with the specific reason and a way to try again.
///
/// Destructive and non-destructive failures are told apart by colour and by what the
/// primary action offers, because "the PC is asleep" and "your key was rotated" need
/// completely different responses.
class EsiErrorView extends StatelessWidget {
  const EsiErrorView({
    super.key,
    required this.message,
    this.detail,
    this.onRetry,
    this.retryLabel = 'Try again',
    this.secondaryAction,
    this.accent = EsiPalette.alarm,
  });

  final String message;
  final String? detail;
  final VoidCallback? onRetry;
  final String retryLabel;
  final Widget? secondaryAction;
  final Color accent;

  @override
  Widget build(BuildContext context) => Center(
        child: Padding(
          padding: const EdgeInsets.all(EsiSpace.xl),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Container(
                width: 54,
                height: 54,
                decoration: BoxDecoration(
                  shape: BoxShape.circle,
                  color: accent.withValues(alpha: 0.1),
                  border: Border.all(color: accent.withValues(alpha: 0.3)),
                ),
                child: Icon(Icons.warning_amber_rounded, size: 24, color: accent),
              ),
              const SizedBox(height: EsiSpace.lg),
              Text(
                message,
                style: EsiType.subtitle,
                textAlign: TextAlign.center,
              ),
              if (detail != null) ...<Widget>[
                const SizedBox(height: EsiSpace.sm),
                ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 400),
                  child: Text(
                    detail!,
                    style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
                    textAlign: TextAlign.center,
                  ),
                ),
              ],
              const SizedBox(height: EsiSpace.xl),
              Wrap(
                spacing: EsiSpace.md,
                runSpacing: EsiSpace.md,
                alignment: WrapAlignment.center,
                children: <Widget>[
                  if (onRetry != null)
                    FilledButton(onPressed: onRetry, child: Text(retryLabel)),
                  ?secondaryAction,
                ],
              ),
            ],
          ),
        ),
      );
}

/// A quiet placeholder used while a slice loads for the first time, so the layout does not
/// jump when data lands.
class EsiLoading extends StatelessWidget {
  const EsiLoading({super.key, this.height = 180, this.label});

  final double height;
  final String? label;

  @override
  Widget build(BuildContext context) => SizedBox(
        height: height,
        child: Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              const SizedBox(
                width: 22,
                height: 22,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
              if (label != null) ...<Widget>[
                const SizedBox(height: EsiSpace.md),
                Text(label!, style: EsiType.body.copyWith(color: EsiPalette.faint)),
              ],
            ],
          ),
        ),
      );
}

/// A shimmering block for skeleton rows. One animation, used only while first data loads.
class EsiSkeleton extends StatefulWidget {
  const EsiSkeleton({super.key, this.width, this.height = 13, this.radius = 4});

  final double? width;
  final double height;
  final double radius;

  @override
  State<EsiSkeleton> createState() => _EsiSkeletonState();
}

class _EsiSkeletonState extends State<EsiSkeleton> with SingleTickerProviderStateMixin {
  late final AnimationController _controller = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 1400),
  )..repeat(reverse: true);

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => AnimatedBuilder(
        animation: _controller,
        builder: (context, _) => Container(
          width: widget.width,
          height: widget.height,
          decoration: BoxDecoration(
            color: Color.lerp(
              EsiPalette.edge,
              EsiPalette.hull,
              _controller.value,
            ),
            borderRadius: BorderRadius.circular(widget.radius),
          ),
        ),
      );
}

/// A confirmation dialog.
///
/// Every irreversible action in this app goes through here, and the copy always says what
/// will happen on the PC — not just what the button is called. `destructive` switches the
/// accent to the alarm colour so the difference is visible before the finger lands.
class EsiConfirmDialog extends StatefulWidget {
  const EsiConfirmDialog({
    super.key,
    required this.title,
    required this.body,
    this.confirmLabel = 'Confirm',
    this.cancelLabel = 'Cancel',
    this.destructive = false,
    this.details = const <Widget>[],
    this.requiresTyping,
  });

  final String title;
  final String body;
  final String confirmLabel;
  final String cancelLabel;
  final bool destructive;
  final List<Widget> details;

  /// When set, the confirm button stays disabled until this exact text is typed. Used for
  /// the actions that can lose someone's work — a force restart with unsaved documents.
  final String? requiresTyping;

  static Future<bool> show(
    BuildContext context, {
    required String title,
    required String body,
    String confirmLabel = 'Confirm',
    bool destructive = false,
    List<Widget> details = const <Widget>[],
    String? requiresTyping,
  }) async {
    final result = await showDialog<bool>(
      context: context,
      barrierDismissible: false,
      builder: (context) => EsiConfirmDialog(
        title: title,
        body: body,
        confirmLabel: confirmLabel,
        destructive: destructive,
        details: details,
        requiresTyping: requiresTyping,
      ),
    );
    return result ?? false;
  }

  @override
  State<EsiConfirmDialog> createState() => _EsiConfirmDialogState();
}

class _EsiConfirmDialogState extends State<EsiConfirmDialog> {
  final TextEditingController _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  bool get _phraseMatched {
    final phrase = widget.requiresTyping;
    if (phrase == null) return true;
    return _controller.text.trim().toUpperCase() == phrase.toUpperCase();
  }

  @override
  Widget build(BuildContext context) {
    final accent = widget.destructive ? EsiPalette.alarm : EsiPalette.signal;
    return AlertDialog(
      backgroundColor: EsiPalette.deck,
      shape: const RoundedRectangleBorder(borderRadius: EsiRadius.dialog),
      title: Row(
        children: <Widget>[
          Container(
            width: 3,
            height: 20,
            decoration: BoxDecoration(color: accent, borderRadius: EsiRadius.chip),
          ),
          const SizedBox(width: EsiSpace.md),
          Expanded(child: Text(widget.title, style: EsiType.title)),
        ],
      ),
      content: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 420),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Text(widget.body, style: EsiType.body.copyWith(color: EsiPalette.dim)),
            if (widget.details.isNotEmpty) ...<Widget>[
              const SizedBox(height: EsiSpace.lg),
              for (final detail in widget.details) detail,
            ],
            if (widget.requiresTyping != null) ...<Widget>[
              const SizedBox(height: EsiSpace.lg),
              Text(
                'Type ${widget.requiresTyping} to continue.',
                style: EsiType.body.copyWith(color: EsiPalette.dim, fontSize: 12.5),
              ),
              const SizedBox(height: EsiSpace.sm),
              TextField(
                controller: _controller,
                autofocus: true,
                textCapitalization: TextCapitalization.characters,
                style: EsiType.mono.copyWith(color: EsiPalette.ink),
                decoration: InputDecoration(hintText: widget.requiresTyping),
                onChanged: (_) => setState(() {}),
              ),
            ],
          ],
        ),
      ),
      actionsPadding: const EdgeInsets.fromLTRB(EsiSpace.lg, 0, EsiSpace.lg, EsiSpace.lg),
      actions: <Widget>[
        TextButton(
          onPressed: () => Navigator.of(context).pop(false),
          child: Text(widget.cancelLabel),
        ),
        FilledButton(
          style: FilledButton.styleFrom(
            backgroundColor: accent,
            foregroundColor: EsiPalette.void_,
            disabledBackgroundColor: EsiPalette.edge,
            disabledForegroundColor: EsiPalette.faint,
          ),
          onPressed: _phraseMatched ? () => Navigator.of(context).pop(true) : null,
          child: Text(widget.confirmLabel),
        ),
      ],
    );
  }
}
