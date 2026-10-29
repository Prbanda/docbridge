// Small inline icon set (stroke icons, currentColor) so the UI has no icon-font dependency.
type IconProps = { size?: number; className?: string };

const base = (size: number) => ({
  width: size,
  height: size,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth: 1.8,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
  'aria-hidden': true,
});

export const Logo = ({ size = 20 }: IconProps) => (
  <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden>
    <rect x="3" y="4" width="4" height="16" rx="1" fill="currentColor" />
    <rect x="9" y="4" width="4" height="16" rx="1" fill="currentColor" opacity="0.85" />
    <rect x="15" y="4" width="5" height="16" rx="1" fill="currentColor" opacity="0.7" />
  </svg>
);

export const ChevronRight = ({ size = 14, className }: IconProps) => (
  <svg {...base(size)} className={className}><path d="m9 6 6 6-6 6" /></svg>
);

export const ChevronDown = ({ size = 14, className }: IconProps) => (
  <svg {...base(size)} className={className}><path d="m6 9 6 6 6-6" /></svg>
);

export const TeamIcon = ({ size = 16 }: IconProps) => (
  <svg {...base(size)}>
    <circle cx="9" cy="8" r="3" />
    <path d="M3.5 19a5.5 5.5 0 0 1 11 0" />
    <circle cx="17" cy="9" r="2.3" />
    <path d="M15.5 14.2A4.5 4.5 0 0 1 21 18.5" />
  </svg>
);

export const BinderIcon = ({ size = 16 }: IconProps) => (
  <svg {...base(size)}>
    <rect x="5" y="3.5" width="14" height="17" rx="1.5" />
    <path d="M9 3.5v17" />
  </svg>
);

export const FolderIcon = ({ size = 16 }: IconProps) => (
  <svg {...base(size)}><path d="M3.5 7.5a2 2 0 0 1 2-2h3.8l2 2.2h7.2a2 2 0 0 1 2 2v7.8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z" /></svg>
);

export const FileIcon = ({ size = 16 }: IconProps) => (
  <svg {...base(size)}>
    <path d="M7 3.5h7l4.5 4.5v11a1.5 1.5 0 0 1-1.5 1.5H7A1.5 1.5 0 0 1 5.5 19V5A1.5 1.5 0 0 1 7 3.5z" />
    <path d="M14 3.5V8h4.5" />
  </svg>
);

export const SearchIcon = ({ size = 16 }: IconProps) => (
  <svg {...base(size)}><circle cx="11" cy="11" r="6.5" /><path d="m20 20-4.2-4.2" /></svg>
);

export const CloseIcon = ({ size = 12 }: IconProps) => (
  <svg {...base(size)}><path d="M6 6l12 12M18 6 6 18" /></svg>
);

export const CheckIcon = ({ size = 12 }: IconProps) => (
  <svg {...base(size)} strokeWidth={3}><path d="m5 12.5 4.5 4.5L19 7.5" /></svg>
);
