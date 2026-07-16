import './globals.css';
import type { Metadata } from 'next';
import AppChrome from '@/components/layout/AppChrome';
import React from 'react';
import { Inter } from 'next/font/google';

const inter = Inter({
  subsets: ['latin', 'cyrillic', 'cyrillic-ext'],
  display: 'swap',
});

export const metadata: Metadata = {
  title: 'Admin | Kirma.sh',
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="ru">
      <body className={`${inter.className} h-screen antialiased text-gray-900`}>
        <AppChrome>{children}</AppChrome>
      </body>
    </html>
  );
}
