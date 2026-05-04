'use client';
import { createContext, useContext, useState, ReactNode } from 'react';

export type TopbarButtonVariant = 'primary' | 'secondary' | 'danger';

export type TopbarButton = {
  icon?: React.ReactNode;
  label: string;
  onClick?: () => void;
  disabled?: boolean;
  variant?: TopbarButtonVariant;
  iconOnly?: boolean;
  position?: 'left' | 'right';
};

export type TopbarPage = {
  title: string;
  subtitle?: string;
} | null;

interface TopbarContextType {
  topbarButtons: TopbarButton[];
  setTopbarButtons: (buttons: TopbarButton[]) => void;
  topbarPage: TopbarPage;
  setTopbarPage: (page: TopbarPage) => void;
}

const TopbarContext = createContext<TopbarContextType | undefined>(undefined);

export const TopbarProvider = ({ children }: { children: ReactNode }) => {
  const [topbarButtons, setTopbarButtons] = useState<TopbarButton[]>([]);
  const [topbarPage, setTopbarPage] = useState<TopbarPage>(null);
  return (
    <TopbarContext.Provider
      value={{ topbarButtons, setTopbarButtons, topbarPage, setTopbarPage }}
    >
      {children}
    </TopbarContext.Provider>
  );
};

export const useTopbar = (): TopbarContextType => {
  const context = useContext(TopbarContext);
  if (!context) {
    throw new Error('useTopbar must be used within a TopbarProvider');
  }
  return context;
};
